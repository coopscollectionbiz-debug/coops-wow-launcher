# Coop's WoW Launcher v1.2.0

The launcher can now download missing required patches for the supported Coop's HD enUS 3.3.5a client. Use Check / Install Patch; matching files are kept, and only missing dependencies and required updates download.

Includes automatic provisioning for all 26 required HD patch archive destinations, the current custom patch-z, and the existing addon packages. Large archives download as resumable parts and are verified after assembly. Every payload retains SHA-256 verification. Installation remains transactional with backups, rollback and interrupted-update recovery.

Three redundant/inactive archive files may be absent. Existing changed baseline archives, unsupported executable/base clients and conflicting override archives remain blocked. This release does not qualify Reforged or other HD packs and does not include the stock WoW client or executable.

The Windows x64 launcher ZIP includes its .NET runtime. Extract it before running the launcher. Existing v1.1 users must download this new launcher binary to use automatic prerequisite downloads.

Download **Coops-WoW-Launcher-v1.2.0-win-x64.zip**, extract it, run the launcher, choose your supported HD client folder, and click **Check / Install Patch**. You do not need to download individual MPQ or part files manually.
