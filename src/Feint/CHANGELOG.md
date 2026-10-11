# Changelog

## 0.3.0

- New `KeepCombo` setting (off by default): your combo carries on if your next swing starts within `ComboKeepSeconds` (1 second by default), also after a dodge or block. Vanilla resets it after 0.2 seconds. Cancelling a swing before it hits still resets the combo. A kept combo's next swing turns toward your aim by up to 90°, like a combo's first swing does in vanilla (which turns all the way).

## 0.2.1

- `DodgeAfterHit` now works for attacks in `NonCancellableAttacks` too, such as the knife's jump attack. That setting only stops cancels before the hit.
- A dodge after the hit pressed in the air starts when you land.

## 0.2.0

- New `DodgeAfterHit` setting (off by default): dodge after a swing has hit to skip the rest of it.
- New `DodgeAfterHitPenalty` setting (on by default): charge extra stamina for that dodge, scaled like other cancels.

## 0.1.0

- Initial release.
