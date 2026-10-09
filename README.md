# HF LDMOS Amplifier — Zeus community feature

Control and metering for a network-connected HF LDMOS amplifier from inside Zeus.

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
Calibration (power = counts²/450, current = counts × 0.135) was fitted to one screenshot and is
approximate. Field 5 of the status push has never been seen non-zero.

## Build and test

    cd web && npm run build && cd ..
    dotnet build src/Zeus.Community.HfAmp/Zeus.Community.HfAmp.csproj -c Release
    dotnet run tests/HfAmp.Tests.cs          # 30 tests against a mock amplifier
    cd src/Zeus.Community.HfAmp && pwsh ./build-package.ps1   # script lives beside the .csproj, as in the template

License: GPL-2.0-or-later.
