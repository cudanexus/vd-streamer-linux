# Virtual Desktop Streamer for Linux (unofficial port)

[![Featured by Awesome REA](https://raw.githubusercontent.com/reacher-z/Awesome-REA/477658fa73f6860e86cd1fc526657c2efdc25194/assets/featured-by-awesome-rea-inline.svg)](https://awesomerea.org/resources/vd-streamer-linux-case/)

Stream your Linux desktop, audio and microphone to the **Virtual Desktop** app on Meta Quest,
Pico, Vive, Play for Dream, Android XR and Apple headsets, with keyboard, mouse and
laser-pointer input.

This is an unofficial port and is not affiliated with Virtual Desktop, Inc. It reuses the
Virtual Desktop protocol libraries from the official **macOS** streamer and replaces the macOS
layer (screen capture, video encoding, audio drivers, input, UI) with native Linux code. To the
headset and the Virtual Desktop matchmaking service it looks like the macOS streamer, so it has
the macOS feature set: desktop streaming and video playback. It does not support SteamVR or
OpenXR games, which only the Windows streamer provides.

> **Unofficial project.** It is not affiliated with or endorsed by Virtual Desktop, Inc.
> "Virtual Desktop" is a trademark of its owner. This repository contains **no** Virtual Desktop
> code or binaries: the build extracts the protocol libraries from the official installer that
> you download yourself, and packages built with them are for your own use only. You need your
> own copy of Virtual Desktop on the headset. See [Legal](#legal).

This port was built by Claude Code (Claude Opus) with the REA reverse-engineering tools, from a
single prompt plus a second prompt for fixes after real-world use. See
[What has been done so far](#what-has-been-done-so-far) for the full history, including the
1.1 fixes for lag that built up over time, switching networks, scrolling and CPU use.

| Feature | Linux implementation |
| --- | --- |
| Discovery, pairing, remote (internet) connections, encryption | Vendor `VirtualDesktop.Net` (unchanged) |
| Desktop capture | xdg-desktop-portal ScreenCast + PipeWire (Wayland: GNOME, KDE, Sway, Hyprland…) or X11 (XRandR + XShm) |
| Video encoding | GStreamer: NVIDIA NVENC, VA-API (AMD/Intel), x264, OpenH264; H.264 and HEVC |
| Keeping latency low | Network pacing and adaptive bitrate (new in 1.1), unchanged-frame skipping on X11 |
| Multiple monitors | One encoded stream per monitor (up to the headset's limit) |
| PC audio to headset | PulseAudio / PipeWire (`VirtualDesktopSpeakers` virtual sink) |
| Headset microphone | PulseAudio / PipeWire virtual source `VirtualDesktopMic` |
| Keyboard, mouse, laser pointers, scrolling | `/dev/uinput` virtual devices (X11 and all Wayland compositors) |
| Clipboard (headset to PC) | wl-clipboard / xclip |
| Video files on the headset ("Videos" tab) | Vendor FFmpeg-based player, using system FFmpeg 7 |
| Settings UI | Local web page at `http://127.0.0.1:38899` (`vdstreamer ui`) and a CLI |

## Install

### Build the packages

You need the official macOS installer, `VirtualDesktop.Streamer.Setup.pkg`, from
<https://www.vrdesktop.net>. You can also let the build download it. Building works on Linux
or macOS. A private .NET 8 SDK is downloaded automatically if none is installed.

```bash
./build.sh --pkg /path/to/VirtualDesktop.Streamer.Setup.pkg            # x64 + arm64: .tar.gz, .deb, .rpm
./build.sh --download --arch x64 --formats deb                         # latest release, one format
./build.sh --no-vendor ...                                             # without Virtual Desktop's libraries
```

Packages land in `dist/`. Building `.deb` and `.rpm` files needs `dpkg-deb` and `rpmbuild`, or
Docker. Packages built with the vendor libraries contain Virtual Desktop's proprietary code and
are for your own use only; do not redistribute them. Packages built with `--no-vendor` download
the libraries from vrdesktop.net the first time the service starts.

### Debian / Ubuntu / Mint / Pop!_OS

```bash
sudo apt install ./dist/vdstreamer_*_amd64.deb
```

Use `apt install ./file.deb`, not `dpkg -i`: apt installs the dependencies in the same step.
Since 1.1 the package also installs on **Ubuntu 20.04** without `apt -f install` (it accepts
OpenSSL 1.1). Read [Ubuntu 20.04](#ubuntu-2004) for what works there.

To upgrade, install the new `.deb` the same way, then restart the service:
`systemctl --user restart vdstreamer`.

### Fedora / RHEL / openSUSE

Enable RPM Fusion for the x264 encoder (`gstreamer1-plugins-ugly`), or rely on OpenH264 or your
GPU encoder:

```bash
sudo dnf install ./dist/vdstreamer-*.x86_64.rpm
```

### Arch / Manjaro / EndeavourOS

```bash
cd packaging/arch && makepkg -si      # put VirtualDesktop.Streamer.Setup.pkg here to skip the download
```

### Any other distribution (tarball)

```bash
tar xf dist/vdstreamer-*-linux-x64.tar.gz && sudo ./vdstreamer-*/install.sh
```

Runtime dependencies: GStreamer 1.16+ (base, good, bad, plus ugly for x264), the GStreamer
PipeWire plugin for Wayland, libpulse and `pactl`, libX11/libXrandr, OpenSSL 1.1 or 3.
Recommended: xdg-desktop-portal plus the backend for your desktop, a VA-API or NVENC GStreamer
plugin, FFmpeg 7 (for video files), wl-clipboard or xclip, and libnotify.

## First run

1. Log out and back in, or run `systemctl --user start vdstreamer`. The package enables the user
   service for every graphical session; to opt out, run `systemctl --user disable vdstreamer`.
2. Add the account that is signed in on your headset:

   ```bash
   vdstreamer account add meta <your Meta user name>      # or pico / vive / google / pfd / apple
   ```

   You can also connect from the headset first and approve the pairing request with
   `vdstreamer pair accept` or in the web UI. A desktop notification appears when a request
   arrives.
3. Open Virtual Desktop on the headset. Your PC appears in the Computers list.
4. On Wayland, the first connection shows the desktop's screen-sharing dialog. Select all
   monitors you want to stream. The choice is remembered. Use **Choose screens again** in the
   UI to reset it.

`vdstreamer diagnose` checks everything that is needed and tells you which hardware encoder
package to install for your GPU. `vdstreamer test-capture` and `vdstreamer test-audio` exercise
capture/encoding and audio without a headset.

```
vdstreamer [run]                 start the streamer (normally done by the systemd user service)
vdstreamer ui                    open the settings page (starts the streamer if needed)
vdstreamer status                connection status, encoder, bitrate/frame statistics, pairing requests
vdstreamer account add|remove|list
vdstreamer pair list|accept|deny [ID|all]
vdstreamer settings              list settings;  vdstreamer set KEY VALUE  to change one
vdstreamer encoders              GStreamer encoders found on this machine
vdstreamer update [--check]      update the protocol libraries to the latest official release
vdstreamer diagnose | test-capture [SECONDS] | test-audio | version
```

## Settings

Change settings with `vdstreamer set KEY VALUE` or in the web UI.

| Setting | Default | Meaning |
| --- | --- | --- |
| `preferredEncoder` | `auto` | `auto`, `nvenc`, `va`, `vaapi`, `x264` or `openh264` |
| `preferredCodec` | `automatic` | `automatic`, `h264` or `hevc` |
| `captureBackend` | `auto` | `auto`, `portal` or `x11` |
| `maxFramerate` | `0` | Frame rate limit, for example `30` to halve encoding work. `0` uses what the headset asks for. |
| `skipUnchangedFrames` | `true` | X11: don't encode the screen again while nothing on it changes |
| `adaptiveBitrate` | `true` | Lower the bitrate while the network can't keep up, then raise it again |
| `scrollSpeed` | `1.0` | Scroll speed multiplier. `1.0` matches the Windows streamer; a negative value inverts the direction. |
| `autoAdjustBitrate` | `true` | Let the headset pick the bitrate from its bandwidth test |
| `allowRemoteConnections` | `true` | Allow connections over the internet (UPnP / NAT traversal) |
| `headsetOnlyAudio`, `streamAudio`, `autoSelectMicrophone`, `lockComputer` | | Audio routing and session options |

## Performance: CPU and GPU use

**Why the official app is lighter.** On Windows, Virtual Desktop captures the desktop with
DXGI Desktop Duplication: the picture stays in GPU memory. NVENC, AMF or Quick Sync converts the
colours and encodes it right there. On macOS, ScreenCaptureKit hands an IOSurface to the
VideoToolbox hardware encoder in the same way. In both cases the CPU barely touches the
picture, and new frames are only produced when the screen changes.

**What happens on Linux:**

| Step | X11 (most Ubuntu 20.04 desktops) | Wayland portal |
| --- | --- | --- |
| Capture | XShm copies the whole screen from the X server (a GPU-to-RAM read with modern drivers) for every frame | PipeWire delivers frames only when the screen changes |
| Colour conversion RGB → YUV | CPU (`videoconvert`), or GPU with VA-API | same |
| Encoding | x264 on the CPU, unless a VA-API/NVENC plugin is installed | same |

Software encoding (x264) of a 1080p desktop at 60 fps needs one to two CPU cores. When another
program is busy with the CPU, frames are encoded late, which feels like lag. A GPU encoder fixes
most of this.

**What 1.1 does to keep it down:**

- **Unchanged frames are skipped (X11).** A frame identical to the previous one is dropped
  before conversion and encoding. In the Ubuntu 20.04 test, a static screen sent 19 frames in
  12 seconds instead of 369. Frames keep flowing for a quarter second after each change
  so the picture sharpens, then one frame per second. A static desktop (reading, typing
  pauses) now costs almost nothing beyond the screen copy, like ScreenCaptureKit on the Mac.
- **Scaling and colour conversion run on up to four cores**, and scaling happens first when the
  headset gets a smaller picture than the monitor.
- **Bitrate adapts to the network**, so the encoder doesn't work on frames that would only queue
  up.
- **`vdstreamer diagnose` names the package to install** for your GPU.

**What you can do:**

- **Intel or AMD GPU:** `sudo apt install gstreamer1.0-vaapi` plus the VA-API driver
  (`intel-media-va-driver` or `i965-va-driver`, or `mesa-va-drivers` for AMD). Then run
  `vdstreamer set preferredEncoder vaapi` (or `va` on GStreamer 1.22+).
- **NVIDIA GPU:** NVENC needs the proprietary driver and GStreamer 1.18 or newer with the
  `nvcodec` plugin, which Ubuntu 22.04 and later have. Ubuntu 20.04's GStreamer 1.16 has no NVENC
  element, so it falls back to x264.
- **Use fewer frames:** `vdstreamer set maxFramerate 30`, or pick a lower frame rate or
  resolution in the headset's streaming settings.
- **Check the numbers:** `vdstreamer status` shows the encoder, the current and target bitrate,
  frames sent, unchanged frames skipped and frames held back by the network.

## Ubuntu 20.04

Tested in an Ubuntu 20.04 container (GStreamer 1.16.3, libinput 1.15.5, OpenSSL 1.1.1f): the
`.deb` installs with `apt install`, desktop video (X11), PC audio, the microphone, keyboard,
mouse, laser pointer and scrolling work. Things to know:

- The default session is X11, which is what the port uses there. Ubuntu 20.04's PipeWire 0.2 is
  too old for Wayland screen sharing.
- Only x264 is installed by default (CPU). Install `gstreamer1.0-vaapi` for Intel/AMD
  (see above); NVENC isn't available with GStreamer 1.16.
- The **Videos** tab (playing PC video files on the headset) needs FFmpeg 7, which 20.04 doesn't
  have. Desktop streaming is not affected.

## Keeping it up to date

The Virtual Desktop server only accepts the current streamer release. When the headset reports
that an update is needed, or the server rejects the version, the streamer downloads the latest
official macOS package from vrdesktop.net, extracts and patches the protocol libraries into
`~/.local/share/vdstreamer/vendor`, and restarts. You can also run `vdstreamer update`.

## Firewall

Allow TCP 38810-38845 and UDP 38810-38850. The packages ship a firewalld service
(`sudo firewall-cmd --add-service=vdstreamer --permanent`) and a ufw profile
(`sudo ufw allow vdstreamer`).

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Headset doesn't list the PC | Same LAN and subnet? Firewall (above)? Account name correct (`vdstreamer account list`)? |
| PC disappears after switching Wi-Fi/LAN | Fixed in 1.1: the streamer notices the new network within seconds and announces itself again. If it doesn't, check the log for "Network changed" and run `systemctl --user restart vdstreamer`. |
| Connection over the internet (other network) fails | `allowRemoteConnections` must be `true`, and the router needs UPnP or port forwarding of TCP 38810-38845 / UDP 38810-38850 to this PC. |
| Video gets laggy after a while | Fixed in 1.1 (see below). If it still happens, check `vdstreamer status`. "Frames held back by the network" growing means the Wi-Fi is the limit: use 5 GHz/6 GHz, or a lower bitrate. CPU at 100% means the encoder is the limit: install a GPU encoder or set `maxFramerate 30`. |
| Scrolling too slow/fast or reversed | `vdstreamer set scrollSpeed 2` (or `0.5`, or `-1` to invert) |
| Black screen on Wayland | Install `xdg-desktop-portal` and the backend for your desktop (`-gnome`, `-kde`, `-wlr`, `-hyprland`), plus `gstreamer1.0-pipewire`. |
| No input from the headset | `vdstreamer diagnose` must report access to `/dev/uinput`. Log in again after installing, or run `sudo modprobe uinput`. |
| High CPU use | See [Performance](#performance-cpu-and-gpu-use). |
| "Streamer needs to be updated" | `vdstreamer update` |
| Logs | `~/.local/share/vdstreamer/logs/StreamerLog.txt` (it contains "Stream statistics" lines after each session); for more detail run with `VDSTREAMER_LOG_LEVEL=Debug`. |

## Limitations

These limitations come mostly from the macOS streamer this port is based on:

- SteamVR and OpenXR games are not supported. They need the Windows streamer.
- The Mac app's virtual extra displays, display-mode switching and rotation are not
  implemented. Real monitors can be streamed (up to four).
- Gamepad emulation is not available, the same as on macOS.
- Only text clipboard sharing is supported; images and files are not.
- Apple Vision Pro's separate cursor layer is not drawn. The cursor is embedded in the video
  instead.
- Absolute pointer positioning assumes the streamed monitors make up the whole desktop on
  Wayland. On a multi-monitor Wayland setup, share all monitors.
- Video-file playback needs FFmpeg 7 (`libavcodec.so.61`). Desktop streaming works with any
  FFmpeg.

## What has been done so far

### 1.0: the port

1. **Analysed both official installers with REA** (details under
   [How it was built](#how-it-was-built)). The Windows streamer is .NET Framework/WPF with
   native Windows drivers and can't be ported. The macOS streamer is .NET 8, and its protocol
   libraries are platform-neutral apart from three macOS calls.
2. **Reused the macOS protocol libraries.** They are extracted from the `.pkg` (XAR +
   gzip/cpio), and those three calls are patched with Mono.Cecil. Members removed by Apple's
   trimming are restored so the libraries load on the normal Linux .NET runtime.
3. **Ported the macOS app's logic to C#:** server registration, LAN discovery and pairing, the
   four streaming channels, desktop and video-file streaming, settings sync and updates.
4. **Wrote the Linux platform layer:**
   - Capture: portal/PipeWire and X11.
   - Encoding: GStreamer, with NVENC, VA-API, x264, OpenH264 and x265.
   - Audio: PulseAudio/PipeWire virtual speaker and microphone.
   - Input: uinput keyboard, mouse and pointer.
   - Clipboard, notifications and sleep inhibition.
   - A local web UI and the `vdstreamer` CLI.
5. **Packaged it** as .deb, .rpm, an Arch PKGBUILD and a tarball for x64 and arm64. The
   packages include a systemd user service, a uinput udev rule, firewall profiles and a desktop
   entry.
6. **Tested it without a headset:** a simulated headset (real discovery, encryption, the four
   channels, input and microphone) plus a mock screen-sharing portal. Seven scenarios passed on
   Debian 13, and the packages installed on Debian 13, Ubuntu 24.04 and Fedora 41.

### 1.1: fixes after first use on Ubuntu 20.04

Reported: it worked on Ubuntu 20.04 after `apt -f`, but became laggy after a while, had problems
when changing networks, scrolling didn't work, and it used a lot of CPU/GPU.

- **Lag that built up over time.**
  - *Cause:* the vendor library writes each video frame straight into the TCP socket. When
    Wi-Fi throughput drops below the video bitrate (interference, distance, another device
    downloading), Linux lets the socket's send buffer grow (up to 4 MB by default), so frames
    queue up in the kernel. The delay grows with the buffer, which is larger on Wi-Fi than on
    the test link, where it reached 0.3–0.6 s. It stays there as long as the link is slower
    than the stream. PC audio could pile up the same way in the PulseAudio record buffer.
  - *Fix:* before each frame, the streamer checks how much data is still unacknowledged in
    the socket (`SIOCOUTQ`) and waits while more than ~100 ms of video is queued. Meanwhile
    GStreamer drops captured frames, so the next frame sent is a fresh one: the frame rate
    drops briefly instead of the delay growing. If the network keeps falling behind, the
    encoder bitrate drops by 30% steps and climbs back after 5 calm seconds
    (`adaptiveBitrate`). The encoder output queue was reduced from 8 frames to 1. PC audio
    waiting to be sent is capped at ~50 ms; older audio is dropped.
  - *Measured* with the simulated headset on an 8 Mbit/s link and a 24 Mbit/s stream, on
    Ubuntu 20.04, in four runs. With pacing, the queueing delay over the last 30 frames averaged
    1–24 ms and the bitrate settled at 5.7 Mbit/s. Without pacing it averaged 258–337 ms and was
    still growing. Pacing also delivered 2.8 times more frames (about 667 against 239 in 25
    seconds).
- **Changing networks.**
  - *What 1.0 already did:* it registered again when the PC's address changed while no headset
    was connected. A test that moves the PC to another LAN passes with both 1.0 and 1.1.
  - *What was wrong:*
    - When the network went away during a session, the dead connection lingered until it
      timed out: about 40 seconds in the test, and up to ~15 minutes of TCP retransmissions
      in the worst case. Meanwhile the PC was not discoverable, so the headset couldn't
      reconnect.
    - A new network that hands out the same IP address (common with home routers) was not
      noticed at all.
    - A dead connection to the Virtual Desktop server, which internet/remote connections go
      through, was not noticed either.
  - *Fix:*
    - The streamer identifies the network by its addresses plus the router's MAC address and
      checks it on every netlink event and every 10 seconds.
    - On a change it restarts discovery and registers again with the server.
    - A headset session whose address has gone away is ended at once. In the test the PC was
      discoverable again in under a second, instead of after ~40 seconds, and the headset
      reconnected on the new network.
    - All streaming sockets now get TCP keepalive and a 10 second `TCP_USER_TIMEOUT`, and the
      server connection a 30 second one, so a dead link is noticed quickly.
- **Scrolling.**
  - *Most likely cause* (the events did reach libinput in 1.0 too, so the problem is further
    up): while a laser pointer is used, its movement goes through the absolute
    "Virtual Desktop Pointer" device, but thumbstick scrolling went through the relative
    "Virtual Desktop Mouse" device. On X11 (Ubuntu 20.04's default), every switch between
    devices resets XInput2 smooth scrolling, and GTK, Firefox and Chrome ignore the first
    scroll event after a switch. Since the devices alternated every frame, every scroll event
    was dropped.
  - *Fix:* scrolling now comes from whichever device moved the pointer last. The speed is
    taken from the Windows streamer, found by decompiling its input service: 8/120 of a wheel
    notch per step, where the old code used 40/120 (five times too fast). Older libinput (20.04) gets classic wheel
    clicks once a notch has accumulated; newer libinput gets smooth high-resolution scrolling.
    The new `scrollSpeed` setting adjusts the speed.
- **CPU/GPU use.**
  - Unchanged frames are skipped on X11.
  - Scaling and colour conversion are multi-threaded, with scaling before conversion.
  - New `maxFramerate` setting.
  - `diagnose` recommends the right hardware encoder package for your GPU.
  - See [Performance](#performance-cpu-and-gpu-use) for why the official app is lighter.
- **Ubuntu 20.04 packaging.** The `.deb` failed to install because it required OpenSSL 3.
  It now accepts `libssl1.1`, and `apt install ./vdstreamer_*.deb` resolves everything.
- **Smaller changes.**
  - `vdstreamer status` and the web UI show bitrate, frame and network statistics.
  - The log records "Stream statistics" after each stream.
  - The systemd unit no longer sets `Nice=-5`, which an unprivileged user service can't use.
- **New tests** (`tests/e2e/run-deb.sh`) run against the installed `.deb` on Ubuntu 20.04:
  - video and audio;
  - keyboard, mouse, microphone, and laser pointer + scroll, checked per device with `evtest`
    and through libinput itself;
  - unchanged-frame skipping;
  - latency on a slow link, with and without pacing.

  `tests/e2e/network-switch.sh` moves the PC between two LANs, with and without a streaming
  headset.

### Not verified yet

- No real headset was available. Everything was tested with the simulated headset.
- No real Wi-Fi was available. The network fixes were tested with a throttled link and by
  moving a container between two Docker networks. Same-address network switches and the
  Virtual Desktop server connection were not tested, because the tests stay off the internet.
- No GPU encoder (NVENC, VA-API) was available in the test environment.
- The scroll fix was checked at the device and libinput level. An X11 app receiving the
  scroll events was not observed directly.

## How it was built

The two official installers were analysed with REA:

- **Windows** (`VirtualDesktop.Streamer.Setup.exe`): an Advanced Installer bootstrapper. Its
  file table lists two 7-Zip archives (first 512 bytes XOR'd with `0xFF`) holding a .NET
  Framework / WPF streamer with native DXGI, NVENC/AMF, SteamVR and driver components. It
  cannot be ported. The `VirtualDesktop.Service.msi` inside holds `VirtualDesktop.Server.exe`,
  whose `InjectScrollInput` gave the scroll scale used since 1.1 (`mouse_event(WHEEL, step * 8)`).
- **macOS** (`VirtualDesktop.Streamer.Setup.pkg`): a XAR package whose payload is a
  gzip-compressed cpio archive holding a **.NET 8** app. The protocol, networking and settings
  libraries (`VirtualDesktop.Core/Net/Interfaces/Mobile.Shared/WCF/FFmpeg`, `Xenko.*`) are
  platform-neutral. Only the app assembly and one helper daemon are macOS-specific.

Findings that shaped the port:

- `inspect_managed_native_boundaries`: `VirtualDesktop.Core`, `Mobile.Shared` and `FFmpeg`
  declare no P/Invokes. `VirtualDesktop.Net` declares 10, into SystemConfiguration, IOKit and
  CoreFoundation.
- `inspect_managed_members` on `VirtualDesktop.Net` (evidence `ev_93114dad…`): exactly three
  call sites reach the macOS bindings: `NetworkInterfaceHelper..cctor` (`CFString.CreateNative`),
  `GetActiveAdapters` and `IsUsbAdapter`. `VendorPatcher` rewrites those with Mono.Cecil at
  install time and verifies that no other reference remains.
- `inspect_managed_members` again for 1.1: `NetVideoClient.WriteFrame` writes synchronously to
  the socket stream (evidence `ev_e573d78d…`), which led to the pacing fix. `RawPointerState` has
  no scroll fields; scrolling arrives only in `RawMouseState.ScrollX/ScrollY` (evidence
  `ev_2b50d90b…`).
- The bundled assemblies were trimmed together with Apple's copy of the .NET base library, so
  some interface and abstract members are missing (for example
  `BindableBase.add_PropertyChanged`). `TrimRepair` restores them so the types load on the full
  Linux runtime. It rebuilds event accessors on their backing fields and gives other members
  default bodies.
- Because of the same trimming, the vendor DLLs reference `System.Private.CoreLib` directly. A
  generated compile-time facade forwards those types to the reference assemblies.
- `FFmpeg.AutoGen` 7.0.1 (evidence `ev_9c1c8d2f…`) is pointed at the system FFmpeg libraries.

The protocol flow mirrors the decompiled macOS app:

- Server registration (`IComputerRegistry.RegisterComputer5`).
- LAN discovery on UDP 38850: an AES-wrapped RSA key exchange carrying the `Computer` record.
- The four TCP channels: messaging 38810, data 38820, video 38830 and audio 38840.
- A 5 MB bandwidth probe.
- `VideoFormat` and `FrameMetadata` / `SliceHeader` packets, plus 48 kHz S16 audio samples.

## Testing without a headset

`tests/HeadsetSim` is a simulated headset built on the vendor libraries. It performs LAN
discovery, the RSA/AES key exchange, the four channels and the bandwidth probe, publishes headset
settings, and can play a library video. It records the H.264/HEVC stream it receives.

| Option | What it does |
| --- | --- |
| `--input` | Sends keyboard and mouse input |
| `--scroll` | Scrolls with the thumbstick while a laser pointer moves |
| `--mic` | Sends microphone audio |
| `--read-mbps N` | Reads the video like a slow Wi-Fi link |

The simulated headset reports the queueing delay of the frames it receives.
`tests/portal-mock` provides a mock xdg-desktop-portal so the Wayland path can be tested
headless.

```bash
tests/e2e/run.sh <published build with vendor/> <published HeadsetSim>          # Debian 13, 7 scenarios
tests/e2e/run-deb.sh <image with the .deb installed> <published HeadsetSim> <vendor dir>   # e.g. Ubuntu 20.04
tests/e2e/network-switch.sh <image with the .deb installed> <published HeadsetSim> <vendor dir> [idle|session]
```

## Legal

- This is an independent, unofficial interoperability project for Linux, which the official
  streamer does not support. It is not affiliated with, endorsed by or supported by
  Virtual Desktop, Inc. Please don't contact Virtual Desktop support about it.
- The code in this repository is MIT-licensed (see [LICENSE](LICENSE)). The Virtual Desktop
  protocol libraries it uses are proprietary and are **not** part of this repository. `build.sh`
  and `vdstreamer setup` extract them from the official macOS installer downloaded from
  vrdesktop.net. Packages built with them must not be redistributed.
- The headset app is a paid product. This project does not bypass any purchase, licence check or
  DRM.
- If you represent Virtual Desktop, Inc. and have concerns, please open an issue.

## Layout

```
src/VDStreamer/            the streamer (.NET 8)
  Vendor/                  pkg extraction (XAR + cpio), Cecil patch, trim repair, assembly loading
  Streamer/                connection, discovery/pairing, session, desktop and video streaming (ported flow),
                           NetTuning.cs (socket pacing, adaptive bitrate, TCP timeouts)
  Linux/                   capture (portal/PipeWire, X11), GStreamer encoders, audio, uinput, clipboard
  Web/                     local settings UI and JSON API
src/VendorTool/            build-time vendor extraction and compile facade
tests/HeadsetSim/          simulated headset
tests/portal-mock/         mock ScreenCast portal
tests/e2e/                 container end-to-end tests (Debian 13, installed .deb e.g. Ubuntu 20.04)
packaging/                 systemd user unit, udev rule, desktop file, deb/rpm/Arch/tarball
```
