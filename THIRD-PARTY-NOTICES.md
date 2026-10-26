# Third-party notices

Ptty PC Test Monitor (MIT, see `LICENSE`) includes the following third-party software, embedded unmodified in
`PttyPcTestMonitor.exe`. Each component remains under its own license. The full license texts are in the
`licenses` folder.

| Component | Version | License | License text | Source |
|---|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | `licenses/MPL-2.0.txt` | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| BlackSharp.Core | 1.0.7 | MPL-2.0 | `licenses/MPL-2.0.txt` | https://github.com/Blacktempel/BlackSharp |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | `licenses/MPL-2.0.txt` | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | `licenses/MPL-2.0.txt` | https://github.com/Blacktempel/RAMSPDToolkit |
| HidSharp | 2.6.4 | Apache-2.0 | `licenses/HidSharp-LICENSE.txt` | https://www.zer7.com/software/hidsharp |
| System.Memory, System.Buffers, System.Numerics.Vectors, System.Runtime.CompilerServices.Unsafe, System.CodeDom, System.Security.AccessControl, System.Security.Principal.Windows, System.Threading.AccessControl | — | MIT | `licenses/dotnet-LICENSE.txt`, `licenses/dotnet-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/runtime |

The MPL-2.0 components are distributed in executable form only. Their source code is available at the
locations above.

PawnIO (https://pawnio.eu), the driver used for CPU/RAM temperatures and motherboard fans, is **not** bundled.
The app offers to install it through `winget` (package `namazso.PawnIO`) with the user's confirmation.
