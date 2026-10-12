Name:           vdstreamer
Version:        %{pkgversion}
Release:        %{pkgrelease}
Summary:        Virtual Desktop Streamer for Linux (unofficial port)
License:        Proprietary (Virtual Desktop protocol libraries) + MIT (Linux port)
URL:            https://www.vrdesktop.net
AutoReqProv:    no
Requires:       glibc, libstdc++, zlib, openssl-libs, ca-certificates
Requires:       gstreamer1, gstreamer1-plugins-base, gstreamer1-plugins-good, gstreamer1-plugins-bad-free
Requires:       pipewire-gstreamer, pulseaudio-libs, pulseaudio-utils, libX11, libXrandr, systemd
Recommends:     gstreamer1-plugins-ugly, gstreamer1-plugin-openh264, xdg-desktop-portal, wl-clipboard, libnotify
Recommends:     gstreamer1-vaapi, (libavcodec-free or ffmpeg-libs), (gstreamer1-plugins-ugly or gstreamer1-plugin-openh264)
Suggests:       yt-dlp

%description
Streams the Linux desktop, audio and microphone to the Virtual Desktop app on Meta Quest,
Pico and other headsets, with keyboard/mouse/controller input. Reuses the Virtual Desktop
protocol libraries from the official macOS streamer with a native Linux capture, encode and
input layer. Not affiliated with or supported by Virtual Desktop, Inc.

%install
mkdir -p %{buildroot}
cp -a %{stage}/. %{buildroot}/

%post
modprobe uinput 2>/dev/null || :
udevadm control --reload-rules 2>/dev/null || :
udevadm trigger --subsystem-match=misc --sysname-match=uinput 2>/dev/null || :
systemctl --global enable vdstreamer.service >/dev/null 2>&1 || :

%preun
if [ $1 -eq 0 ]; then
  systemctl --global disable vdstreamer.service >/dev/null 2>&1 || :
fi

%files
/usr/bin/vdstreamer
/usr/lib/vdstreamer
/usr/lib/systemd/user/vdstreamer.service
/usr/lib/udev/rules.d/60-vdstreamer-uinput.rules
/usr/lib/modules-load.d/vdstreamer-uinput.conf
/usr/share/applications/vdstreamer.desktop
/usr/share/icons/hicolor/scalable/apps/vdstreamer.svg
/usr/lib/firewalld/services/vdstreamer.xml
%config(noreplace) /etc/ufw/applications.d/vdstreamer
%config(noreplace) /etc/xdg/autostart/vdstreamer.desktop
%doc /usr/share/doc/vdstreamer/README.md

%changelog
* Mon Oct 12 2026 vd-streamer-linux <noreply@localhost> - %{pkgversion}-%{pkgrelease}
- Linux port package
