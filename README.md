# RZ1ZR C5Max LDMOS Amplifier Control — Zeus community feature

Control and metering for a network-connected HF LDMOS amplifier with a RZ1ZR controller from inside Zeus.

* Live output / reflected / input power, drain current, SWR, supply, temperature
* **Efficiency %** (estimate: output power ÷ (supply V × current))
* **Band following**: the amp is moved to the band the radio is tuned to (edge-triggered,
  so a manual band choice sticks until the radio changes band; never switches while transmitting)
* Band, antenna, supply low/high, cooling, bypass and reset controls (locked while transmitting)
* Hands the amp back to its front panel (command `99`) when Zeus shuts down

The plugin only *reads* the radio (`ReadRadioState`); it never retunes or keys it.

## Protocol (decoded from packet captures, not vendor documentation)

TCP 5005: two-ASCII-digit commands (`11` hello, `31`–`34` antenna, `71`–`78` band,
`41`/`42` supply, `45`/`46` fans, `21`/`22` bypass, `23` reset, `99` release); the amp pushes
`temp,volts*10,band,antenna,flag,tx,fanHigh,bypass` on every change.
UDP 5010: send `11`; while transmitting the amp streams `fwd,refl,cur,inp` raw counts (~20 Hz).
Calibration (power = counts²/475.3, current = counts × 0.2051) was fitted to side-by-side readings of the
amplifier's own front panel (forward 42–388 W, reflected 1–29 W, current 0.9–20.5 A) and is good to about 3 %. Input
power has not been compared. The panel current is about 1.5× what the vendor application shows. Field 5 of the status push is the fault code: 0 when healthy, 3 in a captured high-SWR fault (the only code seen; others are shown as "Fault code N").

## Meter colour zones

Green / yellow / red. These are the defaults; every meter's minimum, maximum and yellow/red thresholds can be changed in
Settings (gear icon > Meter ranges and colours) and are stored by Zeus, so they suit any DIY LDMOS amplifier. Output, input,
supply and SWR defaults are the owner's; current and temperature are derived and should be reviewed:

| Meter | Green | Yellow | Red |
| --- | --- | --- | --- |
| Output | < 600 W | 600-1199 W | >= 1200 W |
| SWR (and Reflected, which follows SWR) | <= 1.7:1 | 1.7-2.4:1 | >= 2.5:1 |
| Input | < 3 W | 3-4.9 W | >= 5 W |
| Supply | < 48.4 V | 48.4-52.9 V | >= 53 V |
| Current | < 30 A | 30-37.9 A | >= 38 A |
| Temp | < 60 C | 60-74.9 C | >= 75 C |

Current: the BLF189XRA datasheet gives no maximum drain current, so the limits come from the power budget.
1.3 kW at 48-50 V and about 70 % drain efficiency is roughly 37-39 A, and the amp's own gauge ends at 40 A.
Temp: Tj max is 225 C and Rth(j-c) is 0.11 K/W. At 1250 W CW and about 70 % efficiency the device
dissipates about 540 W, or about 59 K from junction to case. Holding Tj to 150 C leaves about 90 C at the case, and
a heatsink sensor reads some 10-15 C lower, so red starts at 75 C. The sensor location is unknown, so adjust
the limits in Settings after watching real readings.

## Faults

When the amplifier reports a fault the panel shows a red banner (with a Reset button), a Fault pill and the card outline,
and the standard meters-pane entry reports the amplifier as not operating. A soft two-note chime plays once when the
fault starts (optionally repeating every 30 s until it clears); volume, on/off and a test button are in Settings. Browsers
only allow sound after one click or key press in the page, and the chime comes from the browser tab or window that runs Zeus.
A fault that is already present when Zeus connects is shown but not announced. Whether Reset clears a fault has not been verified.

## Build and test

    cd web && npm run build && cd ..
    dotnet build src/Zeus.Community.HfAmp/Zeus.Community.HfAmp.csproj -c Release
    dotnet run tests/HfAmp.Tests.cs          # 30 tests against a mock amplifier
    cd src/Zeus.Community.HfAmp && pwsh ./build-package.ps1   # script lives beside the .csproj, as in the template

License: GPL-2.0-or-later.
