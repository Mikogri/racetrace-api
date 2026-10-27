# Research dependencies and redistribution

Verified against installed Python 3.10 Windows-wheel metadata and bundled license
files. The main research environment is pinned as follows; all seven packages
and their Python runtime dependencies are listed explicitly in requirements.txt.
There are no paid APIs, model licenses, GPU requirements or hosted ML services.

| Package | Version | Main license | Redistribution obligations |
|---|---|---|---|
| NumPy | 2.2.6 | BSD-3-Clause | Keep copyright, license, disclaimer and bundled notices. |
| SciPy | 1.15.3 | BSD-3-Clause | Keep copyright, license, disclaimer and bundled notices. |
| scikit-learn | 1.7.2 | BSD-3-Clause | Keep notices; no endorsement using contributor names. |
| joblib | 1.5.2 | BSD-3-Clause | Keep copyright, license and disclaimer. |
| threadpoolctl | 3.6.0 | BSD-3-Clause | Keep copyright, license and disclaimer. |
| LightGBM | 4.6.0 | MIT | Include copyright and permission notice. |
| XGBoost | 3.0.5 | Apache-2.0 | Include license/notices; mark modifications; retain attribution and patent terms. |

Primary license sources: [LightGBM v4.6.0](https://github.com/lightgbm-org/LightGBM/blob/v4.6.0/LICENSE),
[XGBoost v3.0.5](https://github.com/dmlc/xgboost/blob/v3.0.5/LICENSE),
[scikit-learn v1.7.2](https://github.com/scikit-learn/scikit-learn/blob/1.7.2/COPYING),
[NumPy v2.2.6](https://github.com/numpy/numpy/blob/v2.2.6/LICENSE.txt),
[SciPy v1.15.3](https://github.com/scipy/scipy/blob/v1.15.3/LICENSE.txt).

## Transitive and native components

On this Windows environment LightGBM requires NumPy/SciPy; XGBoost requires
NumPy/SciPy; scikit-learn requires NumPy/SciPy/joblib/threadpoolctl. Optional extras
are not installed. `license-inventory.json` records the exact installed `Requires-Dist`
metadata, including conditional/platform extras; an extra listed there is not
necessarily installed. Pip/setuptools are virtual-environment tooling, not model
inference dependencies. Python itself is PSF-licensed; Windows system runtimes
are prerequisites, not redistributed by this research package.

Do **not** reduce binary-wheel licensing to the main-package label. NumPy/SciPy
wheel notices include OpenBLAS/LAPACK (BSD variants) and GCC runtime components
under GPL-3.0 **with the GCC Runtime Library Exception**. These are open-source
licenses compatible with commercial use; they are not all MIT/BSD. Preserve the
full supplied notices and exception. Redistribution of the native runtime
components themselves can carry source/license obligations; do not assume the
runtime exception removes every obligation for every binary. NumPy source notices
also identify MIT/Zlib/Apache-2.0 components and distinguish source-only tools
from installed binary contents. Follow the notices for the exact wheel/platform.

The experiment copies installed `.dist-info` license files verbatim to ignored
`artifacts/incident-ml/licenses/` and records SHA-256 hashes. Keep these full texts
with any redistributed Python environment; package-specific/source-distribution
notices remain authoritative. This is an inventory, not a substitute for a legal
redistribution review or a full native-binary SBOM.

## Race data and optional C# integration

Library licenses do not grant ownership or redistribution rights to ChronoRace,
Tissot, UCI, Pinkbike, source narratives, timing snapshots or curated labels.
Raw snapshots, source documents and trained-model artifacts are not automatically
published. No health narratives are copied into the dataset. Before public model
or dataset release, verify source-data rights separately.

The optional conversion environment was actually installed and tested, separately
from the base requirements. `requirements-onnx.txt` pins all resolved Python
runtime dependencies on this Windows host:

| Package | Version | Main license |
|---|---|---|
| onnxmltools | 1.14.0 | Apache-2.0 |
| onnx | 1.19.1 | Apache-2.0 |
| onnxruntime | 1.23.1 | MIT |
| protobuf | 6.33.0 | BSD-3-Clause |
| ml_dtypes | 0.5.3 | Apache-2.0 |
| typing_extensions | 4.15.0 | PSF-2.0 |
| flatbuffers | 25.9.23 | Apache-2.0 |
| coloredlogs | 15.0.1 | MIT |
| humanfriendly | 10.0 | MIT |
| packaging | 25.0 | Apache-2.0 OR BSD-2-Clause |
| sympy | 1.14.0 | BSD-3-Clause |
| mpmath | 1.3.0 | BSD-3-Clause |
| pyreadline3 (Windows) | 3.5.6 | BSD-3-Clause |

Preserve each package's complete supplied license/notices; Apache components
also carry patent/NOTICE/modification terms, and PSF carries attribution and
change-description terms. Optional runtime native third-party notices remain
applicable: the main MIT license is not a replacement for those notices.

The separate net10.0 console resolved `Microsoft.ML.OnnxRuntime 1.23.1`,
`Microsoft.ML.OnnxRuntime.Managed 1.23.1` and `System.Numerics.Tensors 9.0.0`,
all under MIT main licenses. Exact resolved NuGet dependencies/integrity hashes
are in its `packages.lock.json`. ONNX Runtime supplies `ThirdPartyNotices.txt`
in its NuGet package; preserve that plus LICENSE when redistributing its native
binaries. Conditional dependencies for other target frameworks/platforms can
differ; this inventory describes the actual net10.0 Windows restore, not every
dependency listed under every nuspec target. No production API project reference
or package dependency was added.
