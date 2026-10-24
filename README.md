<a id="readme-top"></a>

<div align="center">
  <h3 align="center">Nomad Drive Tow</h3>

  <p align="center">
    A host-only BepInEx plugin for Nomad Drive: hitch one RV to the back of another and tow it.
    <br />
    <strong>Preview.</strong> Tested solo and with one friend who did not have the mod.
    <br />
    <br />
    <a href="https://github.com/NunoD87/NomadDrive-Tow/releases">Download</a>
    &middot;
    <a href="https://github.com/NunoD87/NomadDrive-Tow/issues/new?labels=bug">Report Bug</a>
    &middot;
    <a href="https://github.com/NunoD87/NomadDrive-Tow/issues/new?labels=enhancement">Request Feature</a>
  </p>
</div>

<details>
  <summary>Table of Contents</summary>
  <ol>
    <li><a href="#about-the-project">About The Project</a></li>
    <li><a href="#getting-started">Getting Started</a></li>
    <li><a href="#usage">Usage</a></li>
    <li><a href="#how-it-works">How It Works</a></li>
    <li><a href="#known-limits">Known Limits</a></li>
    <li><a href="#roadmap">Roadmap</a></li>
    <li><a href="#contributing">Contributing</a></li>
    <li><a href="#license">License</a></li>
    <li><a href="#contact">Contact</a></li>
  </ol>
</details>

## About The Project

Nomad Drive has no way to take a second RV along. This plugin adds one: park two vehicles in a line, press a key, and the heavier one tows the other. Press the key again to let go.

* **Only the host needs the mod.** Friends join as normal and see both vehicles move together. This was tested with one friend who did not have the mod.
* One key (`F2` by default) attaches and releases the tow.
* The towed vehicle just rolls: it is put in neutral, its idle braking is switched off and nobody can drive it while it is hitched.
* The tow lets go by itself if the hitch stretches too far (a crash or a physics glitch).
* If the towed vehicle belonged to another player, it goes back to them when you release it.
* No new items, models or textures. It uses the vehicles that are already in the game.

> **Status: preview (v0.x).** It works in the playtest build it was written against. It has had little testing at speed, with collisions or with many players. Please report anything odd.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Getting Started

### Prerequisites

* Nomad Drive (the playtest build the plugin was written against).
* **BepInExPack_NomadDrive** from Nexus Mods: <https://www.nexusmods.com/nomaddrive/mods/7>
  (BepInEx set up for this game; the game's own core libraries are stripped, so plain BepInEx does not start).

### Installation

1. Extract **BepInExPack_NomadDrive** into the game folder, next to the game `.exe`.
2. Start the game once from Steam, then quit, so BepInEx creates its folders.
3. Download `NomadDriveTow-<version>.zip` from [Releases][releases-url] and extract it into the same game folder.
   You should get `BepInEx/plugins/NomadDriveTow/NomadDriveTow.dll`.
4. Start the game from Steam.

Only the host installs it. Everyone else plays as normal.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Usage

1. Host a game (or play solo).
2. Park two vehicles in a line, facing the same way, with the rear of the front one a few metres from the nose of the other. Release the handbrake if you like.
3. Press **F2**. A message shows on screen: which vehicle tows which, their weights and the gap.
4. Drive the front vehicle. The other one follows.
5. Press **F2** again to release.

The plugin picks the pair whose hitch points are closest: the front vehicle's rear (the Motorcycle Holder position) and the other vehicle's front Tow Knob slot. The tow only starts if:

* you are the host,
* the front vehicle is at least as heavy as the towed one (RV-C 7400 kg can tow a Hippie at 6500 kg, not the other way round),
* both face roughly the same way,
* the hitch points are within 8 m.

**Config** (`BepInEx/config/com.nunod87.nomaddrive.tow.cfg`, created on first run):

| Section / Key | Default | Meaning |
|---|---|---|
| `General` / `TowKey` | `F2` | Key that attaches and releases the tow |
| `General` / `MaxAttachDistance` | `8` | Largest gap (m) between the two hitch points |
| `General` / `CarrierMustBeHeavier` | `true` | Only the heavier (or equal) vehicle may tow |
| `General` / `RequireSameDirection` | `true` | Both vehicles must face the same way |
| `Joint` / `BreakForce` | `0` | Force that snaps the tow (0 = never) |
| `Joint` / `PitchRollLimitDegrees` | `20` | How far the towed vehicle may pitch or roll against the carrier |
| `Joint` / `YawLimitDegrees` | `80` | How far it may swing sideways |
| `Joint` / `AutoReleaseGapMetres` | `4` | Release the tow if the hitch stretches this far (0 = never) |
| `Fixes` / `HostTakesAuthority` | `true` | Host takes network ownership of the towed vehicle while towing, and gives it back |
| `Fixes` / `PrepareTowedVehicle` | `true` | Neutral gear and no idle braking on the towed vehicle (restored on release) |
| `Fixes` / `BlockTowedDriverControls` | `true` | Whoever sits in the towed vehicle cannot drive it |
| `Fixes` / `AllowTowedSteering` | `false` | Let that person still steer |
| `Fixes` / `SkipDriverlessHold` | `true` | Skip the game's "hold still when nobody drives" logic for the towed vehicle |
| `Fixes` / `ForceWheelSimulation` | `true` | Keep the towed vehicle's wheel simulation awake |

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## How It Works

Found by reading the game's assemblies and by logging real sessions:

* Every vehicle already has a **Motorcycle Holder** upgrade zone at the rear and a **Tow Knob** slot at the front. The plugin uses their positions as the hitch points.
* The tow itself is a physics joint (a locked ball joint with limited angles) between the two vehicles, created on the host. Other players only receive the normal position updates, so they need nothing installed.
* The game holds still any vehicle that nobody is driving (`DampDriverlessMotion` pulls its speed to zero) and can brake or lock its wheels. While a vehicle is towed, the plugin skips those holds for that vehicle only, switches off the idle braking, selects neutral and zeroes its driver's pedal input.
* The host takes ownership of the towed vehicle so that the host simulates it, then hands it back to the previous owner on release.

Game types are reached through reflection, so this repository contains no game files.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Known Limits

* **The host drives the front vehicle.** If another player drives the front vehicle, it is simulated on their PC and the host sees it as a kinematic body. That case worked in one test but is not guaranteed.
* High speed with collisions is untested. At speed the joint can carry very large forces, and the tow lets go at 4 m of stretch (default).
* The towed vehicle's engine keeps running if it was running; it can use fuel.
* The tow is not saved. Load a save and the vehicles are separate again.
* It does not carry a vehicle on a rack. For that you would need a new item or model on every player's PC, which this plugin avoids.
* Playtest and release builds can differ. A game update can break the plugin; it logs a warning if it cannot find the expected types or methods.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Roadmap

- [x] Host-only tow with a physics joint
- [x] Free-rolling towed vehicle (neutral, no idle braking, no driver)
- [x] Ownership returns after release
- [x] Automatic release when the hitch stretches
- [ ] Test with a client driving the carrier
- [ ] Test with several players and at speed
- [ ] Re-check against the 27 Oct 2026 release build
- [ ] Optional: stop the towed engine while hitched

See the [open issues](https://github.com/NunoD87/NomadDrive-Tow/issues) for a full list of proposed features (and known issues).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Contributing

Contributions are welcome.

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'feat: add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

Build locally with `dotnet build src/NomadDriveTow/NomadDriveTow.csproj -c Release`.
CI builds every push, and pushing a tag like `v0.5.1` publishes a GitHub Release with the zip.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## License

Distributed under the MIT License. See `LICENSE` for more information.

Not affiliated with Indie Devil. Use at your own risk, and check the playtest terms before modding.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Contact

Nuno Daniel - [@NunoD87](https://github.com/NunoD87)

Project Link: [https://github.com/NunoD87/NomadDrive-Tow](https://github.com/NunoD87/NomadDrive-Tow)

[releases-url]: https://github.com/NunoD87/NomadDrive-Tow/releases
