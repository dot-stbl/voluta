# Voluta.Templates

`dotnet new` project templates for [Voluta](https://github.com/dot-stbl/voluta).

## Install

From NuGet (when published):

```bash
dotnet new install Voluta.Templates
```

From a local pack (repo contributors):

```bash
dotnet pack templates/Voluta.Templates -c Release -o ./artifacts/templates
dotnet new install ./artifacts/templates/Voluta.Templates.*.nupkg
```

From a source folder (no pack):

```bash
dotnet new install ./templates/Voluta.Templates/content
```

## Templates

| Short name | Description |
|------------|-------------|
| `voluta-agent` | Console HITL agent: `AddVoluta`, InMemory checkpoint, one interrupt node, optional MEAI chat stub (offline by default) |

## Create a project

```bash
dotnet new voluta-agent -n MyAgent
cd MyAgent
dotnet run
```

Optional parameters:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `--framework` | `net10.0` | Target framework |
| `--volutaVersion` | `0.4.0` | NuGet version of Voluta packages |

## Uninstall

```bash
dotnet new uninstall Voluta.Templates
# or, if installed from a path:
dotnet new uninstall <path-or-package-id>
```
