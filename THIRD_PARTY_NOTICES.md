# Third-party notices

OpenIMG depends on third-party software. Those components remain under their
own licenses; the MIT license for OpenIMG does not replace or extend them.

This inventory was assembled from the public snapshot's restored NuGet package
metadata and project files. It describes source/build dependencies, not a
verified bill of materials for a particular installer. Test-only packages are
identified separately. For a binary release, check the exact publish output
and carry the full upstream license and notice texts required by each shipped
component.

## Application dependencies

| Component(s) | Version(s) in this snapshot | License / notice |
| --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Microsoft.Extensions.*, Microsoft.EntityFrameworkCore.*, Microsoft.Data.Sqlite.Core, System.Management | 10.0.12 | MIT |
| Microsoft.ML.OnnxRuntime and Microsoft.ML.OnnxRuntime.Managed | 1.24.4 | MIT; retain package license and third-party notices |
| Microsoft.Web.WebView2 | 1.0.4191.47 | Microsoft-provided three-condition BSD-style license; retain its `LICENSE.txt` and `NOTICE.txt` |
| OpenCvSharp4 and OpenCvSharp4.runtime.win | 4.13.0.20260627 | Apache-2.0; the native OpenCV distribution may carry additional notices |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.4.0, 10.0.0, 7.0.0 | Apache-2.0 |
| SQLitePCLRaw.bundle_e_sqlite3, .core, .lib.e_sqlite3, .provider.e_sqlite3 | 2.1.12 | Apache-2.0; SQLite itself is public domain |
| Newtonsoft.Json | 13.0.3 | MIT |
| System.Numerics.Tensors | 9.0.0 | MIT |

The corresponding SPDX license texts are available from the primary sources:

- [MIT License](https://opensource.org/license/mit)
- [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0.txt)
- [SQLite public-domain dedication](https://www.sqlite.org/copyright.html)
- [WebView2 SDK license and notices](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47)
- [ONNX Runtime license and third-party notices](https://github.com/microsoft/onnxruntime/tree/rel-1.24.4)
- [OpenCvSharp source and license](https://github.com/shimat/opencvsharp)
- [Serilog source and license](https://github.com/serilog/serilog)
- [SQLitePCLRaw source and license](https://github.com/ericsink/SQLitePCL.raw)
- [Newtonsoft.Json source and license](https://github.com/JamesNK/Newtonsoft.Json)

## Test-only dependencies

These packages are used by the test project and are not intended to ship in the
application installer:

| Component(s) | Version(s) | License / notice |
| --- | --- | --- |
| Microsoft.NET.Test.Sdk, Microsoft.TestPlatform.* | 17.14.1 | MIT |
| coverlet.collector, Microsoft.CodeCoverage | 6.0.4, 17.14.1 | MIT |
| xunit, xunit.* | 2.9.3, 2.0.3, 1.18.0, 3.1.4 | Apache-2.0, except `xunit.abstractions` whose package metadata links its upstream license |

## Explicit exclusions

- Ultralytics YOLO11n is not part of this public snapshot. The separate private
  installer inputs identify it as AGPL-3.0; it is not covered by OpenIMG's MIT
  license. See [Ultralytics licensing](https://www.ultralytics.com/license).
- The local application icon is not included because its source and permission
  chain are unresolved.
- The .NET runtime and WebView2 Evergreen runtime are separate runtime
  components; their redistribution/runtime terms must be checked against the
  exact binary release configuration.
