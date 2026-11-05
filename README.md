# Drift Rivals

3D LAN racing game for macOS (Apple Silicon), built with Unity 6.3 LTS + URP.

## Play
Open `Builds/DriftRivals.app` (double-click). First launch: allow **Local Network** access when macOS asks (needed for LAN races).

- **Solo Race**: you vs AI rivals (set laps / number of AI in the menu).
- **Host LAN Race**: others on the same Wi-Fi see your race in their menu and click it to join. Then press **Start Race**.
- **Join IP**: if a race doesn't show up, type the IP shown on the host's lobby screen.

## Controls
| Key | Action |
|---|---|
| W / Up | Throttle |
| S / Down | Brake / reverse |
| A D / Left Right | Steer |
| Space | Handbrake (start a drift) |
| Shift (or N) | Nitro (refills while drifting) |
| V | Camera: chase / far / bumper |
| C | Look back |
| R | Reset car onto track |
| Esc | Pause menu |
| F | Toggle fullscreen |
| M | Mute |

Drifting: brake into a corner, tap Space, then steer and hold throttle. Drift points and nitro build up while sliding.

## Sharing with friends' Macs
Zip `Builds/DriftRivals.app` and send it (AirDrop works). Because it isn't notarized, on their Mac they need to
right-click the app, choose **Open**, then **Open** again (or run `xattr -cr DriftRivals.app`).
All players must be on the same Wi-Fi/LAN. Game uses UDP ports 47790 (game) and 47791 (discovery).

## Rebuild after changing code
Close the Unity Editor if it has this project open, then run `./build.sh`.
Or open the project in Unity Hub and use the menu **Drift Rivals → 1. Setup Project / 2. Build macOS App**.

## Credits
- Car: "Car Concept" by Eric Chadwick / Darmstadt Graphics Group, CC-BY 4.0 (Khronos glTF Sample Assets)
- Sky HDRI (Kloofendal 48d Partly Cloudy) and asphalt/grass textures: Poly Haven, CC0
- Engine sound loops: "Racing car engine sound loops" by domasx2, CC0 (OpenGameArt)
- Everything else (track, physics, tyre audio, networking) is generated in code.
