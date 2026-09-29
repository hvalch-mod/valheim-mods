# Unhindered

Running around Valheim without snagging on every bush and log.

## Features

- **Pass-through:** bushes, mushrooms, berry bushes, pickables, crops and saplings no longer block you. You can still pick, chop and hit them.
- **Step-up:** walk up onto low obstacles (logs, rocks, ledges) without jumping or using stamina. How tall depends on how fast you move: 0.7 m walking or crouching, 1.2 m running, 1.5 m sprinting by default (`MaxHeightWalk` / `MaxHeightJog` / `MaxHeightSprint`).
  It tests your real body shape against the obstacle, so it works at angles and on odd shapes. `DebugLog` logs why each obstacle was or wasn't stepped on.

Each feature can be turned off separately.

## Multiplayer

Client-side. Works on servers without it. If the server has it, the server's settings are used and locked for non-admins.

## Config

`BepInEx/config/hvalch.Unhindered.cfg`, or in-game with a configuration manager (F1).
