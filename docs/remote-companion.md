# Remote companion

The companion is a separate Windows app whose source lives in `RemoteCompanion/` inside the Full Stack Launcher checkout. Its authenticated management and desktop transports do not use WinRM, TrustedHosts, or Windows account passwords. The existing Windows management, Remote Desktop, and shared-folder routes remain available in **Sections → Remote**.

## Connect another PC

1. Copy `FullStackRemoteCompanion.exe` to the other Windows PC and run it there.
2. Allow the companion on the private LAN using its firewall setup action. This requests administrator approval and creates rules only for the companion's discovery and management ports, restricted to local subnets on Private/Domain profiles.
3. Enable pairing in the companion. It displays a temporary 12-character code.
4. In Launcher, open **Sections → Remote**. Companion PCs appear automatically in **Computers**. If needed, open **Network** to choose the connected adapter or refresh the network list. Select the companion, enter its code, and choose **Pair & connect**. **Scan LAN** finds other devices; **Add PC** opens a hostname or IPv4 address form.
5. On the remote PC, compare the controller fingerprint with the fingerprint displayed by Launcher, then approve that controller. System information and process reads become available after both sides confirm pairing.

Pairings persist using Windows-user DPAPI. Reopening Launcher reconnects to an approved companion without a Windows password. The companion can revoke an approved controller. Launcher's **Forget pairing** action in the companion options menu removes local trust; it does not revoke permission stored on the remote PC.

The selected computer shows its available actions and companion connection status. **Companion setup** in the sidebar contains setup guidance. **Windows sign-in (advanced)** contains the separate WinRM connection form. **Device details** starts collapsed; requesting **System info** or **Processes** reveals the results.

## View and control the signed-in desktop

Run the updated companion tray app in the signed-in Windows session on the other PC. Enable **Allow desktop control** there, then select that paired PC in Launcher and choose **View & control**. Desktop access is a separate, explicit permission on the companion and resets off when its listener restarts. Turning it off or choosing **Stop viewer** ends the current viewer and releases remote input. Stop viewer also turns permission off until you enable it again.

The viewer starts with input control off. Enable **Control**, then click inside the desktop picture to send mouse movement, buttons, wheel scrolling, and keyboard events. **Escape** returns input to the local PC; **Send Esc** sends one Escape press to the remote desktop. Windows-reserved shortcuts such as Ctrl+Alt+Delete remain local; this viewer does not install a global keyboard hook. Changing monitors or stream settings, losing focus, stale frames, and closing the viewer release held input. The companion also releases held input when its input watchdog expires or the connection ends. Input is never replayed after reconnecting.

Use the monitor picker or **Next monitor** to switch displays. The viewer maps clicks to the displayed monitor, including scaling and letterboxing. **100%** displays stream pixels at actual size with scrolling; **Fit** scales to the viewport. **Full screen** expands the viewer. Stream profiles are:

- **Balanced:** up to 30 FPS, 1920-pixel maximum width, JPEG quality 85.
- **Smooth:** up to 60 FPS, 1920-pixel maximum width, JPEG quality 80.
- **Sharp:** up to 30 FPS, 3840-pixel maximum width, JPEG quality 92.

These are maximum capture rates, not promised delivered rates; the viewer reports measured displayed FPS and received JPEG payload Mbps, excluding protocol/TLS overhead. CPU capture/encoding, remote resolution, local decoding, and Wi-Fi capacity all affect actual performance. Aspect ratio is preserved; frames are not upscaled beyond the source monitor.

Frames use binary JPEG over a persistent, certificate-pinned TLS connection, separate from management reads. Capture buffers are reused and the frame interval accounts for capture and transfer time. The client retains only the latest pending frame and coalesces mouse movement; keyboard and button transitions keep their order. Slow links cannot build an unlimited frame or input backlog. This version uses CPU capture and JPEG rather than a hardware video encoder.

Desktop viewing requires an unlocked interactive user session. The optional LocalService background service continues to provide management reads only. UAC secure desktops and the Windows sign-in screen are not controlled; Windows also limits injected input into elevated applications. Existing Windows Remote Desktop remains available for those workflows. See Microsoft's [interactive service model](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services) and [SendInput restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).

## Background availability

Ordinary companion mode runs in the user's tray. Its startup setting applies to that Windows user. Unattended mode is optional: the companion can explicitly install its own Windows service under LocalService, using a protected installation folder and a separate protected identity store. The tray then manages that service through a restricted local pipe. Switching from user mode to service mode uses a new device identity and requires pairing again.

The service provides read-only management while the remote user is signed out. It does not launch developer commands or interact with another user's desktop. Remote Desktop and SMB continue to use Windows permissions and their own authentication prompts.

## Discovery and trust

Discovery uses UDP **45873** and management uses TLS over TCP **45874**. Discovery is limited to directly connected private IPv4 LANs. Advertisements supply an observed address, name, device ID, and certificate fingerprint; they do not establish Windows identity or trust. Pairing requires a temporary random code, certificate-bound HMAC proofs in both directions, and explicit approval on the remote PC. Subsequent connections require the saved device identity and certificate pin, plus possession of the approved controller certificate.

Authenticated reads use bounded, persistent connections. Reconnects revalidate the saved identity and certificate, including after an IP change. Heartbeats and discovery expiry prevent stale advertisements from being presented as a live connection. Pairing is not automatically resent after an uncertain result. Desktop streaming uses the same private LAN boundary and paired controller identity, with one viewer at a time and bounded messages. No arbitrary remote shell, process termination, file transfer, or internet relay is exposed by the network protocol.

Sleep, network loss, firewall restrictions, and isolated Wi-Fi networks can prevent discovery or connection. Internet or routed-network access requires a separate design; this version stays on the private LAN.

## Build

Build only the application projects; repository automated tests remain deferred.

```powershell
dotnet build .\RemoteCompanion\RemoteCompanion.csproj --configuration Release --nologo
.\RemoteCompanion\publish.ps1
dotnet build .\FullStackLauncher.csproj --configuration Release --nologo
.\publish.ps1 -OutputDirectory .\publish\remote-companion-launcher
```

Publishing does not read or embed existing user identities, pairings, passwords, or Launcher settings. To update the other PC, quit its companion tray app, replace its EXE with the new companion publish, and run it again. Existing Windows-user pairing stores remain outside the executable. If a background service is installed, its installed binary and separate identity are managed by the existing service setup actions; copying a tray EXE does not update a running service or grant desktop access to it.

Source/build/publish verification does not establish delivered FPS, keyboard layouts, mixed-DPI monitor mapping, desktop permissions, pairing, Windows service installation, or reconnect behavior on a second physical computer. Those require manual checks on the intended PCs; repository automated tests remain deferred.
