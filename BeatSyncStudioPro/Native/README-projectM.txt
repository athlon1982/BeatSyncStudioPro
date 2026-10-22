projectM.dll — libprojectM v4.0.0-rc1 (Windows x64, shared build)
The MilkDrop music-visualization engine, from the official
projectM-visualizer/projectm pre-built Windows development package
(libprojectM-Windows-x64-shared-v4.0.0-rc1.zip, GitHub release
v4.0.0-rc1 — the only 4.x release that ships a Windows dev package).

License: LGPL-2.1 (GNU Lesser General Public License, version 2.1).
It is used as a separate, replaceable shared library loaded at
runtime; BeatSync Studio Pro does not link it statically.
Source: https://github.com/projectM-visualizer/projectm

Also required beside the exe: glew32.dll (GLEW 2.2.0, BSD/MIT-style
licenses, https://github.com/nigels-com/glew) — an OpenGL extension
loader libprojectM depends on. The MSVC runtime DLLs it needs
(vcruntime140, msvcp140, vcomp140) come from the system VC++
redistributable already installed on the PC.
