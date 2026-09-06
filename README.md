# VProxies for Windows — Fluent desktop 1.0.1

This is an independent Windows proxy-routing prototype. It does not contain, derive from, or redistribute Proxifier code or drivers.

## Included

- Native WPF desktop UI for Windows x64.
- HTTP CONNECT, HTTPS proxy, SOCKS4A and SOCKS5 settings.
- Full-system, rules-only and selected-application modes.
- Real proxy handshake test rather than a port-only check.
- sing-box 1.14 configuration generation with a named `direct` outbound.
- TUN auto-route, route-loop prevention, process rules and DNS hijacking.
- Account sign-in and entitlement check through `https://api.vproxies.app/api/v1/`.
- Loads active Gateways and the proxies authorized for the signed-in account.
- Requests direct source connection data from `/connections` without exposing the Gateway Management API key.
- Displays country/city and obeys catalog host/port visibility policy.
- Lets the user choose HTTP, HTTPS, SOCKS4 or SOCKS5 from the selected proxy's advertised `protocols[]` list and validates the choice again against `/connections`.
- Branded VProxies interface and application/installer icon.
- DPAPI encryption for saved proxy passwords.
- Inno Setup packaging script.
- Fluent dark desktop interface built around the official VProxies brand kit.
- Searchable proxy fleet with country flags, status indicators, and color-coded latency.
- Clear connect/disconnect actions and a cleaned, color-coded activity log.
- Optional DPAPI-protected account and manual proxy password storage.
- Private DNS through proxy is opt-in because it can reduce performance on some upstreams.
- Non-blocking disconnect and shutdown that terminate the complete sing-box process tree.
- Minimize-to-tray support with Open and Exit commands, plus single-instance protection.

## Important prototype limits

- The provided source has not been compiled in this Linux workspace because no Windows/.NET cross-build toolchain is installed here.
- The build script downloads only pinned official sing-box and Wintun archives and rejects either archive if its SHA-256 differs.
- Gateway proxy mode follows the Website/Admin patch 010 direct-delivery contract. Real traffic still requires an active account, an enabled Gateway, an available proxy, and a successful `/connections` response from production.
- Version 1.0.1 connects the device directly to the selected source proxy. It never falls back to the old Gateway relay endpoint.
- Source credentials remain in memory and the temporary sing-box configuration is removed immediately after startup and again during disconnect cleanup.
- The app runs the core under the elevated UI process. A hardened release should move the core into a signed Windows Service with authenticated IPC.
- Code signing is not included. Production installers and binaries should be signed with your own certificate.

## Build on Windows

Requirements:

1. Windows 10/11 x64.
2. .NET 8 SDK.
3. Inno Setup 6.
4. Internet access to GitHub and wintun.net during the first build, or pre-verified offline runtime files.

Open PowerShell in this directory and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build-windows.ps1
```

The installer will be created at:

```text
artifacts\VProxiesSetup-1.0.1-win-x64.exe
```

### Build with GitHub Actions

Push this directory to a GitHub repository, open **Actions → Build VProxies Windows Installer → Run workflow**, then download the `VProxiesSetup-1.0.1-win-x64` artifact. The workflow installs .NET 8 and Inno Setup, downloads the pinned networking components, verifies their hashes, builds the application, and uploads the resulting installer.

## Safety behavior

The generated sing-box configuration always contains concrete `proxy`, `direct`, and `block` outbounds. `route.final` is never empty. `route.auto_detect_interface` is enabled to prevent TUN routing loops. The VProxies and sing-box processes, private networks, and a literal proxy IP are routed directly.

Use only proxy servers that you own or are authorized to use.

## Pinned networking components

| Component | Package | SHA-256 |
|---|---|---|
| sing-box 1.14.0 | `sing-box-1.14.0-windows-amd64.zip` | `3ffb56267da14e287be48bd10cf7e6505260125bad940b75101fbb4d5d58e5d6` |
| Wintun 0.14.1 | `wintun-0.14.1.zip` | `07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51` |

Read `THIRD-PARTY-NOTICES.md` before distributing a compiled installer.
