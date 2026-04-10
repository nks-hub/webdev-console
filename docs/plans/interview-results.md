# Interview Results — NKS WebDev Console GUI & Backend Technology Decision

**Date:** 2026-04-09
**Respondent:** Lukas (solo developer + Claude Code)
**Questions asked:** 30+ interactive questions via AskUserQuestion

---

## Profil respondenta

| Faktor | Odpoved |
|--------|---------|
| Jazyky | C#, PHP, Go, Python, Dart/Flutter (vibe coding) — polyglot |
| Primarni OS | Win + macOS (2 Win PC + 1 macOS), identicky zazitek vsude |
| Pocet lokalnich webu | 15-30 soucasne |
| PHP frameworky | Nette (primarni) |
| Dalsi jazyky pro weby | PHP + Node.js + Python |
| Docker | Na serveru ano, lokalne NE |
| Databaze | MySQL + Redis + dalsi |
| Lokalni domeny | .loc TLD (nks-web.loc, chatujme.loc) |
| DNS dnes | Mix MAMP PRO + rucni hosts edit |
| PHP CLI dnes | Rucne plna cesta, chaos, zadne aliasy |
| Composer | Obcas, taky problem s PHP cestami |
| Vice PC | 2x Windows + 1x macOS, sync konfigurace by potesil |

---

## Motivace a pain points

### Proc novy nastroj?
MAMP PRO je super ale buggy. WAMP a XAMPP jsou shit. Ma konkretni pozadavky co existujici nastroje nesplnuji.

### MAMP PRO pain points (konkretni):
1. **Config corruption** — SQLite DB s prazdnymi hodnotami, dva httpd.conf soubory
2. **Zadne CLI** — vsechno rucne v GUI
3. **SSL problemy** — OpenSSL EC key fail, manualni certs
4. **Alias bug** — pridani aliasu nebo zmena IP interface rozbije vsechny ostatni vhosty
5. **Restart prepis** — pri kazdem restartu prepise opraveny vhost zabugovanym configem
6. **MySQL** — zadne rizeni, zadna sprava
7. **PHP prepinani** — nevytvari CLI aliasy, nejasne ktera verze je default
8. **php.ini pro CLI** — rozdilne nebo zadne
9. **Specificke vhost pozadavky** — problemy s custom konfiguracemi

---

## Technicke pozadavky

### Must-have (V1):
- Start/stop Apache + MySQL + PHP-FPM
- Vhosty + DNS/hosts management
- SSL certifikaty (mkcert, trusted v browseru)
- PHP version switching (per-site + CLI aliasy)
- System tray s menu
- Dark/light theme
- Live dashboard (CPU/RAM grafy)
- GUI plugin system (rozsiritelne GUI)
- Config validace PRED aplikovanim
- Wildcard aliasy (*.xxx.loc)
- CLI + GUI wizard (auto-detect frameworku)
- Spravna php.ini pro CLI verze

### Want (V1 nebo V2):
- 50+ modulu jako FlyEnv
- Built-in auto-updater
- Reverse proxy (Traefik/Caddy)
- Cloudflare Tunnel / ngrok
- Email testing (Mailpit)
- MAMP PRO migrace import
- Composer integrace s PHP verzemi
- Config sync mezi PC

### MySQL sprava:
- Zalohovani
- Monitoring
- Vytvareni DB + uctu
- Import/export

### Workflow noveho projektu:
- GUI wizard (krok za krokem) s auto-detect existujiciho projektu
- CLI alternativa: `wdc new myapp.loc --php=8.2 --db --ssl --nette`
- Auto-detect framework (Nette/WP/Laravel) podle slozky

### PHP verze management:
- Globalni default + per-site override
- CLI aliasy: php74, php82, php84 v PATH
- php.ini pro CLI = spravne nastaveny per verze

---

## Architekturni rozhodnuti

### GUI typ:
- **Preferuje nativni okno** (NE browser-based)
- WebView mu vadi ("neni to prava nativni appka") — Tauri OK kompromis ale idealne bez WebView
- System tray: **must-have**
- Embedded terminal: externi OK, CLI pouziva externe

### Backend daemon:
- **Go ma AV false positive problem** — respondent to resi uz u pLBOT projektu
- **$0 budget na code signing** — neni ochoten platit za EV cert
- Preferuje **jeden jazyk pro daemon + GUI**
- Pokud ne Go: **C# / .NET** je preferovana alternativa

### IPC:
- **gRPC** (typed, streaming) — jasna volba

### Config storage:
- **SQLite** preferovane

### Instalace:
- **Portable + klasicky installer** (oboji)

---

## Dealbreakery

| Dealbreaker | Status |
|-------------|--------|
| C++ learning curve | ELIMINUJE Qt6 C++ |
| Browser-based UI | ELIMINUJE Go+htmx |
| Rust learning curve | SOFT — nezmineno jako dealbreaker |
| JS/TS runtime (Node.js, npm) | ANO — nechce Node.js bloat, npm ekosystem |
| JS ve WebView (Tauri) | TOLERUJE — "Tauri by slo ale vadi mi WebView" |
| AV false positive bez signing | KRITICKE — $0 budget na cert |

---

## Priority

1. **Rozsiritelnost** (plugin system, moduly, API — platforma ne jen tool)
2. **Cross-platform** (Win + macOS + Linux identicky)
3. **Ekosystem / komunita** (vetsi komunita = mene problemu)
4. Stabilita > cool (nechce experimentalni frameworky)

---

## Technology candidates po interview

### GUI framework finalisty:
| Framework | Jazyk | Pro | Proti |
|-----------|-------|-----|-------|
| **Avalonia UI** | C# | Zna C#, MIT, TrayIcon built-in, DataGrid | Mensi komunita nez Flutter |
| **Flutter Desktop** | Dart | 174K stars, hot reload, mobil future | tray community, AV issues, WebView-less |
| **PySide6** | Python | Qt power, vsechno built-in | Interpretovany, bundle size s Nuitka |
| **Slint** | Rust | 3MB binary, royalty-free | No tray, Rust learning |

### Backend daemon finalisty:
| Technologie | Pro | Proti |
|-------------|-----|-------|
| **C# / .NET** | Jeden jazyk s Avalonia, .NET 9 AOT | Process management mene prokazane nez Go |
| **Go** | Overeny POC (wdc-daemon.exe 5.6MB) | AV false positive KRITICKE, $0 signing |
| **Dart** | Jeden jazyk s Flutter | dart:io pro daemon? Nestandardni |
| **Python** | Jeden jazyk s PySide6 | Interpretovany, GIL |

### Full-stack kombinace:
| Stack | Jeden jazyk? | AV riziko | Poznamka |
|-------|-------------|-----------|---------|
| C# Avalonia + C# daemon | ANO | Stredni | Nejsilnejsi kandidat |
| Flutter + Dart daemon | ANO | Stredni | Dart daemon je nestandardni |
| Flutter + Go daemon | NE | Vysoke (Go) | Overeny POC ale AV problem |
| PySide6 + Python daemon | ANO | Nizke (Nuitka) | Interpretovany, bundle |

---

## Gut feeling & finalni tendence

- **Gut feeling:** Flutter (moderni, hot reload, komunita)
- **Ale:** "stabilita > cool"
- **Backend bez Go:** C# / .NET
- **Nejdulezitejsi:** Rozsiritelnost (platforma > tool)
- **Finalni souboj:** Avalonia (C#) vs Flutter (Dart) — chce side-by-side porovnani

---

*Interview proveden 2026-04-09 pres Claude Code AskUserQuestion tool.*
