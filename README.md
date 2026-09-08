# UniFi Port Sync

AMP plugin that keeps your UniFi port forwards in sync with your game server instances. Add an instance, change a port or remove one, and the matching WAN port forward on your UniFi gateway is created, updated or removed to match.

Author: Mecistios. Built for AMP 2.8 (.NET 8).

## What it does

- Reads each AMP instance and the ports it uses (from the instance's `App.Ports`). The ADS controller and suspended instances are skipped.
- Groups an instance's ports per protocol into one forward rule (UDP together, TCP together, and a separate rule when a port needs both).
- Leaves RCON and admin ports closed. Those are never forwarded to the internet.
- Compares that against the forwards already on your UniFi site:
  - a port already forwarded to the same host is left as it is
  - a port forwarded to a different host is reported as a conflict and not touched
  - only the ports that are genuinely missing get a forward created
- Only manages its own rules (matched by a name prefix), so anything you set up by hand is never changed or removed.

## Syncing

The plugin reacts to changes as they happen. It watches AMP's instance config on disk, so creating an instance, changing a port or deleting an instance triggers a sync within a few seconds. Only the changed instance's rules are looked at, so the rest of your forwards stay untouched.

A timer runs in the background as a fallback (default every 15 minutes). It does a full pass over all instances to catch drift, or anything the live sync missed. Set the interval to 0 to turn it off and sync manually only. Changing the interval takes effect after the instance restarts. You can also run a sync at any time with the Sync Now button in the settings.

On Linux the watcher takes one inotify watch per folder under `instances/`. On systems with a low `fs.inotify.max_user_watches` limit it may fail to start; the plugin logs this and falls back to the timer.

## Requirements

- AMP 2.8 or newer.
- A UniFi OS console with a Control Plane API key.
- The plugin reads AMP's `instances.json`, so it has to run on the machine that hosts the instances (the ADS/controller).

## Building

1. Copy `ModuleShared.dll` from your AMP install into `lib/`.
2. Run `dotnet build -c Release`.

The output is `bin/Release/net8.0/UnifiPortSync.dll`. `ModuleShared.dll` is only used to build against and is not shipped, because AMP already provides it at runtime.

## Installing

1. Put `UnifiPortSync.dll` in the instance's plugin folder: `.../instances/<instance>/Plugins/UnifiPortSync/UnifiPortSync.dll`
2. Add the plugin to that instance's load list in `AMPConfig.conf` while the instance is stopped:

   ```
   AMP.LoadPlugins=["UnifiPortSync"]
   ```
3. Start the instance. The log should show `Loaded UnifiPortSync by Mecistios`.

## Licensing

There is no plugin code signing yet, so on a normal licence AMP will not load this unsigned plugin. To run it, activate an AMP Developer licence on the instance next to your normal runtime licence. The developer licence is a companion to your main licence and does not use up instance slots.

The instance (the ADS controller in particular) needs both licences active at the same time. Reactivate with the developer key first, then with your runtime key, and AMP keeps both:

```
ampinstmgr --ReactivateInstance <instance> <developer-key>
ampinstmgr --ReactivateInstance <instance> <runtime-key>
```

The log then shows both licences present and the plugin loads.

## Configuration

Open the instance configuration and go to Instance Deployment, tab Unifi Network Automation. Fill in:

| Setting | Meaning |
|---|---|
| Enable UniFi Port Sync | Master switch. When off, no syncs run and no changes are made. |
| UniFi Controller Host / IP | Hostname or IP of the UniFi OS console. |
| UniFi Controller Port | HTTPS port of the console (usually 443). |
| UniFi API Key | Control Plane API key (stored encrypted). |
| UniFi Site | Site identifier. Use Test Connection to list your sites. |
| Forward Target LAN IP | The LAN IP that game ports should forward to. |
| WAN Interface | WAN 1, WAN 2 or All WANs. For multi-WAN gateways; single WAN setups keep WAN 1. Changing it updates the existing rules on the next sync. |
| Sync Interval (minutes) | How often the fallback timer runs. 0 turns it off. |
| Rule Name Prefix | Prefix for the rules this plugin owns (default `AMP:`). Must not be empty. |
| AMP Data Path | Optional. Leave blank to auto detect `instances.json`. |

## Endpoints

- `TestUnifiConnection` checks the connection and lists your sites.
- `SyncNow(apply)` returns the reconcile plan. Pass `apply=true` to make changes.
- `RunSync` applies a sync and reports a short summary (used by the button).
- `ListUnifiPortForwards` lists the current forwards on the site.
