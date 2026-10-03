# Prerequisites

Install these before running `00-wotlk-azerothcore-orcestrator.ps1`.
Use these exact versions - newer Boost + Visual Studio together can hit a
toolset mismatch that breaks the build.

| Software | Version | Download |
|---|---|---|
| Git for Windows | 2.55.0.2 | https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.2/Git-2.55.0.2-64-bit.exe |
| Visual Studio Community 2022 | v143 toolset (specific build, NOT "Latest") | https://aka.ms/vs/17/release/vs_community.exe|
| CMake | 4.4.0-rc3 | https://github.com/Kitware/CMake/releases/download/v4.4.0-rc3/cmake-4.4.0-rc3-windows-x86_64.msi |
| OpenSSL | Win64 v4.0.2 | https://slproweb.com/download/Win64OpenSSL-4_0_2.exe |
| Boost | 1.84.0, msvc-14.3-64 | https://sourceforge.net/projects/boost/files/boost-binaries/1.84.0/boost_1_84_0-msvc-14.3-64.exe/download |
| MySQL Server | 9.7.1 | https://dev.mysql.com/downloads/mysql/ |

**Visual Studio note:** during install, check "Desktop development with C++",
then go to the Individual Components tab, search "v143", and pick a
**specific numbered build** (not "(Latest)"). This is what keeps Boost's
prebuilt tag matching your compiler.

**OpenSSL note:** when asked "Copy OpenSSL DLLs to", pick "The Windows
system directory".

**MySQL note:** set a root password you'll remember - stage 04 asks for it.

Once all six are installed, run `00-wotlk-azerothcore-orcestrator.ps1` as
Administrator.
