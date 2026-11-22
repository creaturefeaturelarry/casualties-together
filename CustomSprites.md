# CustomSprites

Skin folders read by Casualties Together. Each species has the same layout.

```
CustomSprites/
├── Exp/
│   ├── default/                    slot 0, stock fallback
│   │   ├── Body/
│   │   ├── Head/
│   │   └── WoundView/
│   ├── slot1/                      custom slot
│   │   ├── Body/
│   │   ├── Head/
│   │   └── WoundView/              optional
│   ├── slot2/ ... slot9/           same layout
│   └── backups/
│       └── st3_20261101_191023/
│           ├── Body/
│           ├── Head/
│           └── WoundView/
├── Milky/                          same layout as Exp
├── Dune/                           same layout as Exp
├── backups/                        legacy root location, older builds
└── mp_cache/                       downloaded skin zips, <sha256>.zip
```

## Notes

- Use these exact folder names: `Exp`, `Milky`, `Dune`, and lowercase `default`, `slot1` ... `slot9`.

- `default` is slot 0 on the numpad. The mod does not upload it.

- A slot holds PNGs in `Body` and `Head`. `WoundView` is optional.

- Backup folders use `st<slot>_<YYYYMMDD>_<HHMMSS>`. Current builds write them under `<species>/backups`.

- `mp_cache` keeps at most 32 entries. A full cache drops the oldest first.
