# Bari Visual Studio add-on

A Visual Studio 2026 extension for [bari](https://github.com/p5ych08illy/bari),
a build management tool for .NET suites. It makes Visual Studio's build, clean
and start commands run bari on a solution that bari generated, and keeps the
open solution in sync when bari regenerates projects.

The add-on was started as [zvrana/bari-vs-addon](https://github.com/zvrana/bari-vs-addon)
and is maintained here together with bari.

## Requirements

- Visual Studio 2026 (18.0 or later): Community, Professional or Enterprise.
- A bari build from [p5ych08illy/bari](https://github.com/p5ych08illy/bari)
  with the `AddonSupport` plugin, which ships with bari.

## How it works

`bari vs <product>` generates `target/<product>.sln` and, next to it,
`target/<product>.yaml` for the add-on:

```yaml
bari-path: C:\Bari\bari.exe
goal: debug-x64
target: <product>
startup-path: ...
```

When a solution with such a file is opened, the add-on takes over these
commands:

| Visual Studio command | runs |
|---|---|
| Build Solution, Build Selection | `bari --target <goal> build <product>` |
| Rebuild Solution, Rebuild Selection | `bari --target <goal> rebuild <product>` |
| Clean Solution | `bari --target <goal> clean <product>` |
| Start, Start Without Debugging | a bari build first when one is needed: on the first start, or after files changed |
| Cancel | stops bari and every process it started, at once |

Output goes to the **Build** pane of the Output window. Build, Rebuild and Clean
from a project's context menu are not intercepted; Visual Studio builds that
project with MSBuild.

The add-on loads in the background, so Visual Studio's synchronous autoload
does not need to be allowed. It starts loading when a solution starts opening;
a build started before it has loaded, right after opening a large solution, is
an ordinary Visual Studio build.

The add-on watches `src/` and the suite's `.yaml` files. When files are added or
deleted, or the suite definition changes, it offers to rebuild. For projects
that are not SDK-style, it reloads the projects bari regenerated after a bari
command. It opens files with delete sharing, so it never keeps bari or MSBuild
from deleting or replacing them.

## Options

**Tools › Options › Bari**:

| option | default | meaning |
|---|---|---|
| SetStartUpProject | on | set the startup project when the solution is loaded |
| StartArguments | | default command-line arguments for Start |
| PromptReload | on | ask before reloading modified projects or the solution |
| KeepFilesOpen | on | reopen the open documents after a reload |
| Verbose | off | pass `-v` to bari |
| SoftClean | off | pass `--soft-clean` to `clean` and `rebuild` |
| Logging | off | log to `%LOCALAPPDATA%\Bari\Logs\bari-log.txt` |
| SetManagedDebugger | on | debug the startup project with the managed debugger only |

## Installing

Download `BariVSPackage.vsix` from the latest successful
[Build VSIX](https://github.com/attixray/bari-vs-addon/actions/workflows/build.yml)
run (GitHub wraps it in a zip), close Visual Studio and open the file.

### Upgrading

Every build of the add-on, whoever built it, has the same extension ID,
`102d89df-1d64-4843-a75d-3a67bf3763a2`. The installer replaces an installed
copy only if both of these hold:

- the new version is higher;
- both copies have the same scope. Since 1.12.2 the add-on installs for all
  users. Older versions installed per user, and the installer leaves such a
  copy in place and installs next to it.

So before installing:

1. Close Visual Studio and look under **Extensions › Manage Extensions** for
   every installed *Bari* extension and its version.
2. Uninstall any copy older than 1.12.2, and any copy of the same version you
   are installing.
3. Install the new `.vsix`. Installing for all users needs administrator rights.
4. Check that exactly one copy is left, with the new version.

If the old copy was signed, the installer may also refuse to replace it with
an unsigned build; uninstalling it first avoids that too.

Do not leave two copies with the same ID installed side by side. To list the
installed copies:

```powershell
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
Get-ChildItem "$vs\Common7\IDE\Extensions", "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_*\Extensions" -Recurse -Filter extension.vsixmanifest -ErrorAction SilentlyContinue |
  Select-String '102d89df-1d64-4843-a75d-3a67bf3763a2' -List | ForEach-Object {
    $version = (Select-String -Path $_.Path -Pattern 'Identity [^>]*Version="([^"]+)"').Matches.Groups[1].Value
    "$version  $($_.Path)"
  }
```

### Synchronous autoload

From 1.12.4 the add-on loads in the background, so Visual Studio does not need
to allow synchronous autoload of extensions. Visual Studio 18.10 no longer
shows that option. If an older add-on needs it, it can still be set while Visual
Studio is closed, with `vsregedit.exe` from Visual Studio's `Common7\IDE`:

```powershell
vsregedit.exe set "<Visual Studio installation path>" HKCU AutoLoadPackages AsyncAutoLoadOptOutMicrosoftInternalOnly dword 1   # 0 turns it off again
```

## Building

Open `src/BariVSPackage.sln` in Visual Studio 2026 with the *Visual Studio
extension development* workload, or build from a Developer PowerShell:

```powershell
nuget restore src\BariVSPackage.sln
nuget install src\BariVSPackage\packages.config -OutputDirectory src\packages
msbuild src\BariVSPackage.sln -p:Configuration=Release -p:DeployExtension=false
```

The package is `src\BariVSPackage\bin\Release\BariVSPackage.vsix`. The version is
the `Identity` version in `src/BariVSPackage/source.extension.vsixmanifest`.
