# Editor distribution notices

Iseberg is copyright (c) 2026 Adam Driscoll, licensed under MIT. The complete `LICENSE` is included in the editor nupkg. The accessibility peer is shared Iseberg source; the old workbench remains separate.

Direct packages `Avalonia` **12.1.3** and `Avalonia.AvaloniaEdit` **12.0.0** declare MIT in official NuGet metadata. The producer lock also includes `Avalonia.BuildServices` 11.3.2, `Avalonia.Remote.Protocol` 12.1.3 and `MicroCom.Runtime` 0.11.6, each declaring MIT. Preserve upstream copyright/license and bundled third-party notices when distributing their assemblies.

AvaloniaEdit is a port of AvalonEdit. PowerShell lexical highlighting loads AvaloniaEdit's bundled `Highlighting/Resources/PowerShell.xshd` at runtime under that library's license, without copying/relicensing the implementation. Each control has its own palette; no global definition is modified.

The editor **does not depend on or redistribute System.Management.Automation, Microsoft.PowerShell.SDK, PowerShell modules, Microsoft.Management.Infrastructure or PowerShell-native binaries**. It has no built-in PowerShell parser. Host-owned analysis and completion providers are opt-in; their licenses and behavior are not covered by Iseberg's MIT license.

Headless test consumers explicitly reference `System.Management.Automation` 7.6.6 only to observe a host-owned default runspace and the creation counter. They never use that parser or open that runspace. That **test-only** graph includes `Microsoft.Management.Infrastructure.Runtime.Win` 3.0.0, whose `LICENSE.txt` has Microsoft-specific PowerShell-use and redistribution restrictions rather than MIT. Do not redistribute that test output as an editor distribution. A host choosing its own PowerShell dependency must independently review those terms; the producer removed that dependency rather than promising unsupported static parsing or permissive redistribution.

The native desktop example supplies Avalonia Desktop/Fluent/Skia/HarfBuzz and their runtime assets itself. Review its full lock file, the restored `.nuspec` license fields, included third-party notices and final RID output. Some native assets carry BSD/Apache or bundled licenses; Iseberg's MIT license does not relicense them.

The reviewed native example graph declares MIT expressions except `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419, which supplies a BSD-style `LICENSE` requiring preservation of notices in source/binary distributions and restricting endorsement. Skia/HarfBuzz packages declare MIT at package level but bundle upstream third-party notices as well; preserve those with the host's native assets.

The tested target is net10.0 with the exact direct versions above. No native-free desktop guarantee, macOS/Linux native or screen-reader certification, trim/AOT guarantee or script sandbox is implied. Executing captured text is solely a deliberate host operation, outside this package.
