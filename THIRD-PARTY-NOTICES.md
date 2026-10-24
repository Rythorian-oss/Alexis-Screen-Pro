# Third-Party Notices

Alexis Screen Pro 1.0.0 uses the following open-source components. Their licenses apply to those components; the application source is provided separately by its author.

---

## ScreenRecorderLib 7.0.1

- **Project:** https://github.com/sskodje/ScreenRecorderLib
- **NuGet:** https://www.nuget.org/packages/ScreenRecorderLib/7.0.1
- **Author:** Sverre Kristoffer Skodje
- **License:** MIT

Used for full-display capture, WASAPI audio capture, Media Foundation H.264 encoding, and MP4 multiplexing. This package includes an x64 mixed-mode native assembly and requires the Microsoft Visual C++ 2015–2022 x64 redistributable at runtime.

```text
MIT License

Copyright (c) Sverre Kristoffer Skodje

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## H.NotifyIcon.Wpf 2.3.2

- **Project:** https://github.com/HavenDV/H.NotifyIcon
- **NuGet:** https://www.nuget.org/packages/H.NotifyIcon.Wpf/2.3.2
- **Author:** HavenDV and contributors
- **License:** MIT

Used for the native Windows notification-area icon, rich WPF context menu, tooltips, and balloon notifications.

```text
MIT License

Copyright (c) HavenDV and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## Microsoft .NET / WPF runtime libraries

Alexis Screen Pro targets `net9.0-windows10.0.19041.0` and uses the Microsoft .NET 9 Desktop Runtime and WPF. Those components are distributed by Microsoft under their respective licenses and are not bundled with this source archive.

## Microsoft Visual C++ Runtime

ScreenRecorderLib's native capture assembly requires the Microsoft Visual C++ 2015–2022 Redistributable (x64). It is a prerequisite, not bundled with this source archive.
