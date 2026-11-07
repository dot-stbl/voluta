using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using NSubstitute;

namespace Voluta.Checkpoints.S3.Unit;

/// <summary>
///     Builds an <see cref="IAmazonS3" /> substitute backed by an in-memory object map
///     covering PutObject (including If-Match conditional writes) / GetObject / ListObjectsV2
///     used by <see cref="S3Checkpointer" />.
/// </summary>
internal static class InMemoryAmazonS3
{
    public static IAmazonS3 Create()
    {
        var objects = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var etags = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var client = Substitute.For<IAmazonS3>();

        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var request = callInfo.Arg<PutObjectRequest>();
                var cancellationToken = callInfo.Arg<CancellationToken>();
                var key = request.Key ?? "";
                if (request.IfMatch is { } precondition
                    && (!etags.TryGetValue(key, out var current) || current != precondition))
                {
                    throw new AmazonS3Exception("At least one of the pre-conditions you specified did not hold.")
                    {
                        StatusCode = HttpStatusCode.PreconditionFailed,
                        ErrorCode = "PreconditionFailed",
                    };
                }

                if (request.IfNoneMatch == "*"
                    && objects.ContainsKey(key))
                {
                    // Conditional create: object already exists → 412 PreconditionFailed.
                    throw new AmazonS3Exception("At least one of the pre-conditions you specified did not hold.")
                    {
                        StatusCode = HttpStatusCode.PreconditionFailed,
                        ErrorCode = "PreconditionFailed",
                    };
                }

                var bytes = await ReadBodyAsync(request, cancellationToken);
                objects[key] = bytes;
                var etag = Guid.NewGuid().ToString("N");
                etags[key] = etag;
                return new PutObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    ETag = etag,
                };
            });

        client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<GetObjectRequest>();
                var key = request.Key ?? "";
                return objects.TryGetValue(key, out var bytes)
                    ? Task.FromResult(new GetObjectResponse
                    {
                        BucketName = request.BucketName,
                        Key = key,
                        HttpStatusCode = HttpStatusCode.OK,
                        ETag = etags.GetValueOrDefault(key),
                        ResponseStream = new MemoryStream(bytes),
                    })
                    : throw new AmazonS3Exception("The specified key does not exist.")
                    {
                        StatusCode = HttpStatusCode.NotFound,
                        ErrorCode = "NoSuchKey",
                    };
            });

        client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ListObjectsV2Request>();
                var prefix = request.Prefix ?? "";
                var delimiter = request.Delimiter;

                if (!string.IsNullOrEmpty(delimiter))
                {
                    var common = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var key in objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        var rest = key[prefix.Length..];
                        var slash = rest.IndexOf(delimiter, StringComparison.Ordinal);
                        if (slash >= 0)
                        {
                            common.Add(prefix + rest[..(slash + delimiter.Length)]);
                        }
                    }

                    return Task.FromResult(new ListObjectsV2Response
                    {
                        HttpStatusCode = HttpStatusCode.OK,
                        S3Objects = [],
                        CommonPrefixes = common.OrderBy(static entry => entry, StringComparer.Ordinal).ToList(),
                        IsTruncated = false,
                        KeyCount = 0,
                    });
                }

                var matches = objects.Keys
                    .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .Select(key => new S3Object
                    {
                        Key = key,
                        Size = objects[key].LongLength,
                    })
                    .ToList();

                return Task.FromResult(new ListObjectsV2Response
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    S3Objects = matches,
                    IsTruncated = false,
                    KeyCount = matches.Count,
                });
            });

        return client;
    }

    private static async Task<byte[]> ReadBodyAsync(
        PutObjectRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentBody is not null)
        {
            return Encoding.UTF8.GetBytes(request.ContentBody);
        }

        if (request.InputStream is not null)
        {
            using var memory = new MemoryStream();
            await request.InputStream.CopyToAsync(memory, cancellationToken);
            return memory.ToArray();
        }

        return [];
    }
}
