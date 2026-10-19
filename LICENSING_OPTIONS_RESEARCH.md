# OpenIMG Licensing Options Research

**Research date:** 2026-10-19
**Status:** the owner selected MIT for OpenIMG-authored code and exclusion of the YOLO model from public distribution. This note remains a comparison of options and does not extend MIT to third-party materials.
**Scope:** no-fee licensing choices for OpenIMG-authored software and separate considerations for dependencies, the optional Ultralytics model, and artwork.

> This is informational research, not legal advice. It does not determine who owns every contribution or whether a particular license covers a combined application/model distribution. Resolve those questions with the rights holders and, where needed, qualified counsel.

## Short answer

The standard software licenses below do not require an upfront fee to use, but they grant very different permissions and impose different conditions. “Free” can mean no license fee; it does not mean no attribution, source-sharing, notice, or other compliance obligations. GitHub explains that a public repository without a license remains subject to default copyright rules: visitors may view and fork under GitHub's terms, but do not receive general permission to reuse, redistribute, or make derivative works from the code. [GitHub: Licensing a repository](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)

## No-fee options for code OpenIMG owns

| Option | No-fee? | Practical effect | Main trade-off |
|---|---:|---|---|
| **MIT** | Yes | A short, permissive license allowing use, modification, redistribution, sublicensing, and sale, provided the copyright and permission notice stays with copies/substantial portions. | Easiest to understand and reuse. It does not include Apache-2.0's express patent-license terms. [OSI's MIT text](https://opensource.org/license/mit) |
| **Apache-2.0** | Yes | Permissive use, modification, and redistribution; includes an express patent license and conditions concerning notices and modified files. Apache's application guide describes including the license text and considering a `NOTICE` file. | A little more compliance/notice work than MIT; useful when an explicit patent grant matters. [License text](https://www.apache.org/licenses/LICENSE-2.0.txt) · [Apache application guide](https://www.apache.org/legal/apply-license) |
| **GPL-3.0-or-later** | Yes | Copyleft: when distributing a covered work or derivative, GPL conditions require recipients' freedoms to be preserved and corresponding source to be made available under the applicable GPL terms. The “or later” option must be stated in the project notice; it is not automatic. | Good only if reciprocal source-sharing is an intentional outcome. It is not equivalent to AGPL-3.0, and does not by itself settle the Ultralytics model question. [GNU GPLv3 and application guidance](https://www.gnu.org/licenses/gpl.en.html) |
| **No license / all rights reserved** | No license fee | Retains the default copyright position; it does not grant broad reuse rights. A custom, no-fee permission could be written for a narrow use case, but that would be a bespoke license rather than a standard open-source license. | Appropriate only if reuse/redistribution is not being offered. A public GitHub repository is not itself an open-source license. [GitHub's explanation](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository) |

MIT, Apache-2.0, and GPL are all no-fee options, but they are not interchangeable. The project owner should select one policy for the material they have authority to license; do not add multiple license files as a menu unless intentionally offering dual licensing. GitHub recommends placing the chosen license text in a root-level `LICENSE` file. [GitHub: Licensing a repository](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)

## OpenIMG snapshot facts verified locally

- The clean public snapshot currently has no root `LICENSE` file. Its `LICENSE_DECISION_REQUIRED.md` says no project license is selected and that repository visibility does not grant reuse, modification, or redistribution rights.
- The snapshot excludes `models/` and `src/OpenIMG.App/Assets/OpenIMG.ico`. The application project includes model files only when a sibling `models` directory exists, and includes the ICO only when that file exists. These are conditional build inputs, not proof that their distribution rights are cleared. See `src/OpenIMG.App/OpenIMG.App.csproj`.
- The separate private RC3 installer artifact exists and is 88,310,934 bytes. Its staging directory contains `models/detection/yolo11n.onnx` and `model_manifest.json`; the manifest identifies the model license as `AGPL-3.0` and ONNX Runtime as `MIT`. The installer script packages the RC3 publish directory recursively, excluding only PDB files, and references the local ICO as the setup icon. Thus the inspected RC3 packaging inputs include YOLO; the installer should not be attached to a public release until its licensing and notices are resolved. Evidence was inspected in the private build files `artifacts/installer-staging/0.1.0-rc.3-win-x64/models/detection/model_manifest.json`, `installer/OpenIMG.iss`, and `artifacts/installer/THIRD_PARTY_NOTICES.txt` (the notice file itself describes the prior RC2 package). The full package dependency/runtime license inventory was not audited here.
- The private build's source tree contains an `OpenIMG.ico`, but neither the clean public snapshot nor its documents establish the ICO's authorship or permission chain. The installer script's reference to an icon is not evidence of rights to redistribute it.

These are observations about files present in this workspace at the research date, not a legal conclusion about whether a particular distribution is compliant.

## Third-party components are separate

A project-level license covers only material the licensor has authority to license. It does not replace the license terms for third-party packages, runtime components, models, fonts, icons, or other assets. Each redistributed component must be inventoried and its own license/notice obligations preserved. The local RC3 manifest reports ONNX Runtime as MIT, but the installer also contains other runtime and NuGet components, so that single entry is not a complete bill of materials.

### Ultralytics YOLO11n

Ultralytics' current official licensing guidance says its AGPL-3.0 route is for users comfortable open-sourcing the entire project under AGPL-3.0, and that an Enterprise license is required to use YOLO without open-sourcing the entire project. Its FAQ says this applies to code, models, architectures, training pipelines, and trained/fine-tuned models; it describes source publication for the larger project and related components. This is Ultralytics' stated licensing position; determining its application to OpenIMG's exact use/distribution may require legal advice. [Ultralytics licensing guidance](https://www.ultralytics.com/license)

Accordingly, choosing MIT or Apache-2.0 for OpenIMG does **not** turn the YOLO model into MIT/Apache material or erase the model's AGPL notice. For a no-fee public route, the options to evaluate are: (a) distribute a project/model combination under the AGPL terms Ultralytics requires and satisfy the corresponding obligations, or (b) omit YOLO from the release. A proprietary or permissive OpenIMG license while bundling the model may require a separate Ultralytics Enterprise license, which is paid and therefore not a no-fee option. Do not rely on the fact that the owner uses the app personally or that the source repository is private as a substitute for checking the terms of the actual artifact and distribution.

### Icon and other artwork

Do not apply MIT/GPL/Apache to an icon unless it is software source that the owner controls. Creative Commons says its licenses are not recommended for software, but may be used for separate artistic elements; it also says licensors need authority over the rights they grant. [Creative Commons FAQ](https://creativecommons.org/faq/)

If the icon's copyright owner is confirmed and wants to permit broad reuse, possible no-fee choices for the standalone artwork include:

- **CC0 1.0:** a rights-holder dedication intended to waive copyright and related rights as far as law allows; it does not grant trademark rights or rights belonging to other people. [CC0 1.0 deed](https://creativecommons.org/publicdomain/zero/1.0/)
- **CC BY 4.0:** permits reuse subject to attribution and other conditions; its grant is limited to rights the licensor has authority to grant. [CC BY 4.0 legal code](https://creativecommons.org/licenses/by/4.0/legalcode.en)

Neither CC0 nor CC BY proves that the person applying it owns the artwork. If provenance is uncertain, leave the icon out or replace it with artwork whose source and permission are documented.

## Release checklist after the owner's decision

1. **Confirm authority:** identify who owns or can license each source file and whether any outside contributions have separate terms.
2. **Keep the scope precise:** MIT applies to OpenIMG-authored code and documentation only; third-party components keep their own terms.
3. **Keep YOLO excluded:** the owner selected a no-fee MIT license for OpenIMG and chose not to include the AGPL-licensed YOLO model in the public package.
4. **Clear artwork separately:** document the icon's creator/source and written license or permission before bundling it; do not assume MIT covers it.
5. **Inventory the actual binary:** verify the exact publish output and include required notices for NuGet packages, .NET/runtime files, WebView2-related components, and native binaries.
6. **Keep release materials consistent:** `LICENSE`, README, release notes, and third-party notices must describe the scope and exclusions accurately. The project license does not grant rights to third-party assets or retroactively change terms on previously published copies.

## Sources

- [Open Source Initiative — MIT License](https://opensource.org/license/mit)
- [Apache Software Foundation — Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0.txt)
- [Apache Software Foundation — Applying Apache License 2.0](https://www.apache.org/legal/apply-license)
- [GNU Project — GNU General Public License](https://www.gnu.org/licenses/gpl.en.html)
- [GitHub Docs — Licensing a repository](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)
- [Ultralytics — License](https://www.ultralytics.com/license)
- [Creative Commons — FAQ](https://creativecommons.org/faq/)
- [Creative Commons — CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)
- [Creative Commons — CC BY 4.0 legal code](https://creativecommons.org/licenses/by/4.0/legalcode.en)
