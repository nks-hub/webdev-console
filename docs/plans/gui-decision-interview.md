# DevForge GUI Framework — Rozhodovaci dotaznik

**Ucel:** Systematicky urcit optimalni GUI framework pro DevForge na zaklade realnych potreb, schopnosti tymu a produktovych cilu.

**Jak pouzit:** Odpovezte na kazdu otazku. Po vyplneni sectete body podle scoring guide u kazde otazky. Framework s nejvyssim celkovym skore je vas vitez.

**Kandidati:**

| # | Framework | Jazyk | Predhodnoceni |
|---|-----------|-------|---------------|
| A | Qt6 C++/QML | C++ | 9.5 |
| B | Avalonia UI | C# | 8.5 |
| C | Go + htmx + templ + systray | Go | 9.0 |
| D | PySide6 + Nuitka | Python | 7.5 |
| E | Flutter Desktop | Dart | 7.0 |
| F | Slint | Rust | 6.5 |

---

## SEKCE 1: Tym a dovednosti (vaha 20 %)

**1. Ktery jazyk vas tym ovlada nejlepe?**
- a) C++ (5+ let)
- b) C# / .NET (5+ let)
- c) Go (3+ let)
- d) Python (3+ let)
- e) Dart/Flutter (2+ let)
- f) Rust (2+ let)
- g) Zadny z uvedenych — jsme webovy tym (JS/TS/PHP)

> [SCORING] a-> Qt +4, b-> Avalonia +4, c-> Go+htmx +4, d-> PySide +4, e-> Flutter +4, f-> Slint +4, g-> Go+htmx +2 (nejbliz webu)

**2. Kolik lidi bude na GUI pracovat?**
- a) 1 vyvojar (solo)
- b) 2-3 vyvojari
- c) 4+ vyvojaru

> [SCORING] a-> Go+htmx +3 (nejrychlejsi solo), PySide +2. b-> Avalonia +2, Qt +2. c-> Qt +3, Avalonia +2

**3. Mate zkusenosti s desktopovymi UI frameworky (Qt, WPF, GTK, Swing, ...)?**
- a) Ano, roky praxe
- b) Zakladni zkusenosti
- c) Ne, pouze web nebo mobilni
- d) Ne, zadne UI

> [SCORING] a-> Qt +3, Avalonia +2. b-> Avalonia +2, PySide +2. c-> Go+htmx +3, Flutter +2. d-> Go+htmx +3 (nejmensi UI kod)

**4. Mate zkusenosti s webovym vyvojem (HTML/CSS)?**
- a) Ano, je to nase hlavni domena
- b) Zakladni
- c) Minimalni nebo zadne

> [SCORING] a-> Go+htmx +4 (browser = UI). b-> Go+htmx +2. c-> Qt +1, Avalonia +1

**5. Jaky je vas plan naboru v pristich 12 mesicich?**
- a) Zadny — pracujeme s aktualnim tymem
- b) Hledame C#/.NET vyvojare
- c) Hledame Go vyvojare
- d) Hledame C++ vyvojare
- e) Najimame kde kdo je k dispozici

> [SCORING] b-> Avalonia +3. c-> Go+htmx +3. d-> Qt +3. e-> Avalonia +2 (nejvetsi trh .NET)

**6. Jak moc je tym ochoten se ucit novy jazyk?**
- a) Zadny problem — 2-3 mesice ramp-up akceptujeme
- b) Preferujeme znamy jazyk, ale maly ramp-up OK (do 1 mesice)
- c) Nechceme se ucit novy jazyk, pouzijeme to co umime

> [SCORING] a-> Qt +2, Slint +2, Flutter +1. b-> Avalonia +2, PySide +2. c-> Go+htmx +3 (daemon uz je v Go)

**7. Mate zkusenosti s MVVM/MVC architekturnimi vzory?**
- a) Ano, MVVM (WPF/Avalonia/MAUI)
- b) Ano, MVC (ASP.NET, Rails, Laravel)
- c) Ano, komponentovy pristup (React, Flutter, Vue)
- d) Minimalni

> [SCORING] a-> Avalonia +4. b-> Go+htmx +2. c-> Flutter +3. d-> Go+htmx +2 (nejjednodussi architektura)

**8. Mate zkusenosti s compilovanym buildovacim retezcem (CMake, MSBuild, Cargo)?**
- a) Ano, bezne pouzivame
- b) Zakladni
- c) Ne — preferujeme jednoduche `go build` / `npm build`

> [SCORING] a-> Qt +2, Slint +2. b-> Avalonia +1. c-> Go+htmx +3

**9. Jak dulezity je hot reload behem vyvoje?**
- a) Kriticke — bez toho nedelame
- b) Prijemne, ale ne nutne
- c) Nepotrebujeme

> [SCORING] a-> Flutter +4, Go+htmx +3 (browser hot reload). b-> Avalonia +1, PySide +1. c-> Qt +1

**10. Mate zkusenosti s Go backendem DevForge a jeho JSON-RPC API?**
- a) Ano, jsem autor / hlavni vyvojar
- b) Znam API, ale nekodoval jsem daemon
- c) Neznam

> [SCORING] a-> Go+htmx +3. b-> vsechny +0. c-> vsechny +0

---

## SEKCE 2: Produktova vize (vaha 15 %)

**11. Je DevForge komercni produkt nebo interni nastroj?**
- a) Open-source, komunitni
- b) Open-source core + placene pro funkce
- c) Proprietarni komercni produkt

> [SCORING] a-> Go+htmx +2 (MIT), Avalonia +2 (MIT). b-> Qt +1 (LGPL pozor), Avalonia +2. c-> Qt +2 (LGPL audit nutny), Avalonia +2

**12. Kdo je cilovy uzivatel?**
- a) Zacatecnik (student, junior dev) — musi byt jednoduche
- b) Stredne pokrocily PHP vyvojar
- c) Senior vyvojar / DevOps — oceni power-user funkce
- d) Mix vsech urovni

> [SCORING] a-> Flutter +2 (moderni UI), Avalonia +2. b-> Go+htmx +2, Qt +1. c-> Go+htmx +2 (CLI-first). d-> Qt +2, Avalonia +2

**13. Kolik uzivatelu ocekavate behem prvniho roku?**
- a) Do 500
- b) 500 - 5 000
- c) 5 000+

> [SCORING] a-> vsechny +0. b-> Qt +1, Avalonia +1. c-> Qt +2 (prokazana skalovatelnost)

**14. Jak dulezity je nativni vzhled aplikace (vypadni jako Windows/macOS app)?**
- a) Kriticke — musi vypadat jako nativni systemova appka
- b) Dulezite, ale vlastni branding je OK
- c) Neni dulezite — funkce je priorita

> [SCORING] a-> Qt +4, Avalonia +3. b-> Flutter +2, Avalonia +2, Qt +2. c-> Go+htmx +3

**15. Je akceptovatelne, aby GUI bezelo v prohlizeci (systray ikona -> browser tab)?**
- a) Ano, uplne v poradku — Docker Desktop a Portainer to delaji taky
- b) Akceptovatelne, ale preferuji nativni okno
- c) Ne — musi byt nativni desktopove okno

> [SCORING] a-> Go+htmx +5. b-> Go+htmx +2. c-> Qt +3, Avalonia +3, Flutter +2, PySide +2, Slint +2

**16. Planujete mobilni companion appku v budoucnu?**
- a) Ano, urcite
- b) Mozna nekdy
- c) Ne

> [SCORING] a-> Flutter +4 (sdileny kod), Avalonia +2. b-> Flutter +2. c-> vsechny +0

**17. Jak dulezite je premium/lesteny vizualni dojem?**
- a) Velmi — DevForge musi vypadat profesionalne a moderne
- b) Solidni, ale nemusime soutezit s Figma designem
- c) Utilitarni staci — hlavne at funguje

> [SCORING] a-> Qt +3, Flutter +3, Avalonia +2. b-> Avalonia +2, PySide +1. c-> Go+htmx +3

**18. Chcete custom branding/theming (barvy, logo, vlastni widgety)?**
- a) Ano, plny custom design
- b) Zakladni dark/light theme staci
- c) Systemovy default

> [SCORING] a-> Qt +3 (QML je neomezene), Flutter +3. b-> Avalonia +2, Go+htmx +2 (CSS trivialni). c-> Qt +2, Avalonia +2

**19. Musi aplikace fungovat plne offline?**
- a) Ano — zadna zavislost na internetu
- b) Zakladni funkcionalita offline, updaty online
- c) Internet ocekavan

> [SCORING] a-> vsechny +1 (vsechny funguji offline). b-> vsechny +0. c-> Go+htmx +1 (CDN assets mozne)

**20. Jak dulezity je 'wow factor' pri prvnim spusteni?**
- a) Klicovy — prvni dojem proda produkt
- b) Prijemny, ale sekundarni
- c) Nedulezity

> [SCORING] a-> Qt +3, Flutter +3. b-> Avalonia +2. c-> Go+htmx +2

---

## SEKCE 3: Technicke pozadavky (vaha 25 %)

**21. System tray ikona s menu?**
- a) Must-have (start/stop servicu, quick actions)
- b) Nice-to-have
- c) Nepotrebujeme

> [SCORING] a-> Qt +3, Avalonia +3, Go+htmx +2 (systray lib), PySide +3. b-> vsechny +1. c-> Flutter +1, Slint +1 (nemaji nativni)

**22. Embedded terminal (xterm-like) primo v aplikaci?**
- a) Must-have — nechci otvirat externi terminal
- b) Nice-to-have, ale externi terminal OK
- c) Nepotrebujeme — CLI je v externim terminalu

> [SCORING] a-> Qt +3 (QTermWidget), PySide +2, Go+htmx +3 (xterm.js v browseru). b-> Avalonia +1, Flutter +1. c-> vsechny +0

**23. Real-time log streaming — kolik radku/s ocekavate?**
- a) Nizky provoz (do 100 radku/s)
- b) Stredni (100 - 1000 radku/s)
- c) Vysoky (1000+ radku/s, Apache access log pod zatezi)

> [SCORING] a-> vsechny +1. b-> Qt +2, Avalonia +2, Go+htmx +2 (SSE). c-> Qt +3 (QPlainTextEdit zvlada), Go+htmx +2

**24. Dark/light theme prepinani?**
- a) Must-have
- b) Pouze dark
- c) Pouze light
- d) Nezalezi

> [SCORING] a-> Avalonia +3 (FluentTheme built-in), Qt +2, Go+htmx +2 (CSS media query). b-> vsechny +1. d-> vsechny +0

**25. Typ auto-updateru?**
- a) Built-in v aplikaci (klik -> update -> restart)
- b) Notifikace + manual download
- c) Package manager (winget, brew)

> [SCORING] a-> Qt +2 (QProcess), Avalonia +2 (NetSparkle), Flutter +1. b-> Go+htmx +2 (trivialni). c-> vsechny +1

**26. Preferovany typ instalatoru?**
- a) MSI (Windows Installer) — enterprise standard
- b) MSIX (Microsoft Store ready)
- c) Portable ZIP (bez instalace)
- d) Kombinace MSI + portable
- e) Nezalezi

> [SCORING] a-> Qt +2, Avalonia +2 (WiX built-in). b-> Avalonia +2 (.NET MSIX tooling). c-> Go+htmx +3 (single binary). d-> vsechny +1

**27. Maximalni akceptovatelna velikost instalatoru?**
- a) Do 15 MB
- b) Do 30 MB
- c) Do 60 MB
- d) Nezalezi

> [SCORING] a-> Go+htmx +4 (single binary ~10 MB), Slint +3 (~3 MB), Qt +2 (8-15 MB). b-> Avalonia +2, PySide +2. c-> Flutter +1. d-> vsechny +0

**28. Maximalni akceptovatelna spotreba RAM?**
- a) Do 50 MB
- b) Do 100 MB
- c) Do 200 MB
- d) Nezalezi — uzivatel ma 16+ GB

> [SCORING] a-> Go+htmx +4 (15-30 MB), Qt +3 (30-50 MB), Slint +3. b-> Avalonia +2 (40-80 MB), Flutter +1. c-> PySide +1 (80-120 MB). d-> vsechny +0

**29. Pozadavek na dobu startu aplikace?**
- a) Okamzite (pod 0.5s)
- b) Do 1s
- c) Do 3s
- d) Nezalezi

> [SCORING] a-> Go+htmx +3 (browser uz bezi), Qt +2, Slint +2. b-> Avalonia +2. c-> PySide +1, Flutter +1. d-> vsechny +0

**30. Kolik webu bude uzivatel typicky spravovat soucasne?**
- a) 1-5
- b) 5-20
- c) 20-50
- d) 50+

> [SCORING] a-b-> vsechny +0. c-> Qt +2 (virtualized lists), Avalonia +1, Go+htmx +1. d-> Qt +3 (QML ListView je GPU-accelerated)

**31. phpMyAdmin / databazovy browser?**
- a) Embedded primo v aplikaci
- b) Presmerovani do prohlizece (Adminer/phpMyAdmin)
- c) Nepotrebujeme

> [SCORING] a-> Go+htmx +3 (uz je v browseru — zero cost), Qt +1. b-> vsechny +1. c-> vsechny +0

**32. Rich content rendering (Markdown, syntax highlighting konfiguraku)?**
- a) Must-have
- b) Nice-to-have
- c) Nepotrebujeme

> [SCORING] a-> Go+htmx +3 (highlight.js/Prism), Qt +2 (QSyntaxHighlighter), Avalonia +2 (AvaloniaEdit). b-> vsechny +1. c-> vsechny +0

**33. Podpora vice oken (multi-window)?**
- a) Ano — log okno vedle hlavniho
- b) Taby staci
- c) Jedno okno

> [SCORING] a-> Qt +3, Avalonia +2. b-> vsechny +1, Go+htmx +2 (browser taby zadarmo). c-> vsechny +0

**34. Pristupnost (accessibility / WCAG)?**
- a) Nutna — enterprise zakaznici to vyzaduji
- b) Zakladni (screen reader pro hlavni funkce)
- c) Neni priorita

> [SCORING] a-> Qt +3, Avalonia +2, Go+htmx +3 (nativni HTML a11y). b-> vsechny +1. c-> Flutter +0, Slint +0

**35. Podporu jakych OS verzi potrebujete?**
- a) Pouze aktualni (Win 10+, macOS 13+)
- b) Starsi (Win 7+, macOS 10.15+)
- c) Pouze Windows 10+

> [SCORING] a-> vsechny +1. b-> Qt +3 (nejlepsi zpetna kompatibilita), PySide +2. c-> Avalonia +2

---

## SEKCE 4: Vyvoj a udrzba (vaha 15 %)

**36. Casovy ramec do MVP?**
- a) 3 mesice
- b) 6 mesicu
- c) 12 mesicu
- d) Nespechame

> [SCORING] a-> Go+htmx +4 (3-4 mes. odhad), PySide +2. b-> Avalonia +3 (4-5 mes.), PySide +2. c-> Qt +3 (5-6 mes.), Flutter +2, Slint +2. d-> vsechny +0

**37. Budget na vyvojove nastroje a licence?**
- a) $0 — pouze open-source
- b) Do $500/rok
- c) Do $2000/rok (vcetne code signing)
- d) Neomezeny

> [SCORING] a-> Go+htmx +3 (vse MIT), Avalonia +3 (MIT), Slint +2 (royalty-free tier). b-> vsechny +1. c-> Qt +2 (commercial license mozna). d-> Qt +3

**38. Jak dulezita je velikost komunity a ekosystemu?**
- a) Kriticka — potrebujeme StackOverflow odpovedi a knihovny
- b) Dulezita, ale zvladneme cist zdrojaky
- c) Nepotrebujeme — zvladneme vse sami

> [SCORING] a-> Qt +3 (30 let), Flutter +2 (174K stars), Avalonia +2. b-> vsechny +1. c-> Slint +1, Go+htmx +1

**39. Tolerance vuci breaking changes ve frameworku?**
- a) Nulova — potrebujeme stabilitu na 5+ let
- b) Akceptovatelne pokud je migracni cesta jasna
- c) Nevadi — driv nez framework zastarne, prepliseme

> [SCORING] a-> Qt +4 (30 let zpetne kompatibility), Go+htmx +3 (Go compatibility promise). b-> Avalonia +2. c-> Flutter +1, Slint +1

**40. Kdo bude GUI udrzovat po v1?**
- a) Stejny tym
- b) Jiny tym / komunita
- c) Externi contractor

> [SCORING] a-> vsechny +0. b-> Avalonia +2, Qt +2 (velky talent pool). c-> Avalonia +2 (.NET trh), Qt +1

**41. Jak slozity CI/CD pipeline jste ochotni spravovat?**
- a) Jednoduchy — `go build` / `dotnet publish` a hotovo
- b) Stredni — par kroku, cross-compile
- c) Slozity neni problem

> [SCORING] a-> Go+htmx +4, Avalonia +2 (dotnet publish). b-> Qt +1, Flutter +1. c-> Qt +2, Slint +1

**42. Jak casto planujete velke GUI updaty po v1?**
- a) 1-2x rocne
- b) Ctvrtletne
- c) Kontinualne

> [SCORING] a-> Qt +2 (stabilni). b-> Avalonia +1, Go+htmx +2 (snadne deploye). c-> Go+htmx +3, Flutter +2 (hot reload dev cyklus)

**43. Jste ochotni prispivat upstream fixy do frameworku?**
- a) Ano
- b) Pokud bude nutne
- c) Ne

> [SCORING] a-> Slint +2, Avalonia +2. b-> vsechny +0. c-> Qt +1, Go+htmx +1 (zrale, mene bugu)

**44. Potrebujete podporu starsich OS?**
- a) Windows 7/8 — jeste mame uzivatele
- b) Windows 10+ staci
- c) Multi-platform vcetne Linuxu (Flatpak/AppImage)

> [SCORING] a-> Qt +3. b-> vsechny +1. c-> Qt +2, Flutter +2, Avalonia +2, Go+htmx +2

**45. Potrebujete plugin system primo v GUI (nejen v daemonu)?**
- a) Ano — uzivatel si rozsiri GUI
- b) Mozna v budoucnu
- c) Ne — pluginy jsou jen v daemonu

> [SCORING] a-> Qt +3 (QML plugin loader), Go+htmx +2 (HTML snippety). b-> vsechny +1. c-> vsechny +0

---

## SEKCE 5: Distribuce a uzivatele (vaha 10 %)

**46. Primarni distribucni kanal?**
- a) Web stahovani (GitHub Releases / vlastni web)
- b) Package manager (winget, brew, chocolatey)
- c) Microsoft Store / Mac App Store
- d) Kombinace

> [SCORING] a-> vsechny +1. b-> Go+htmx +2, Qt +1. c-> Avalonia +2 (MSIX), Flutter +1. d-> vsechny +1

**47. Budget na code signing?**
- a) $0 — nepodepisujeme
- b) OV cert ($100-200/rok)
- c) EV cert ($350-700/rok) — SmartScreen reputace
- d) Uz mame

> [SCORING] Neovlivnuje volbu frameworku, ale EV cert je nutnost pro Go binaries (false positive mitigace).

**48. Jak kriticke jsou antivirus false positives?**
- a) Showstopper — nasi uzivatele to vyleka
- b) Problem, ale dokumentaci zvladneme
- c) Nasi uzivatele to znaji

> [SCORING] a-> Avalonia +2 (.NET binaries mene problematicke), Qt +1. b-> vsechny +0. c-> Go+htmx +0

**49. Planujete telemetrii / analytiku v aplikaci?**
- a) Ano — crash reporting + usage stats
- b) Opt-in analytika
- c) Ne

> [SCORING] a-> Avalonia +1 (Sentry .NET), Qt +1 (Crashpad). b-> vsechny +0. c-> vsechny +0

**50. Lokalizace (vicejazycnost)?**
- a) Pouze anglicky
- b) Anglicky + cestina
- c) 5+ jazyku

> [SCORING] a-> vsechny +0. b-> vsechny +0. c-> Qt +3 (Qt Linguist), Avalonia +1, Flutter +1

**51. Enterprise deployment (GPO, MDM, silent install)?**
- a) Ano — mame firemni zakazniky
- b) Mozna v budoucnu
- c) Ne

> [SCORING] a-> Qt +2, Avalonia +2. b-> vsechny +1. c-> Go+htmx +1

**52. Primarni platforma?**
- a) Pouze Windows
- b) Windows + macOS
- c) Windows + macOS + Linux

> [SCORING] a-> Avalonia +2, Go+htmx +2. b-> Qt +2, Avalonia +2. c-> Qt +3, Flutter +2, Go+htmx +3

**53. Linux distribuce — ktere?**
- a) Nepotrebujeme Linux
- b) Pouze Ubuntu/Debian
- c) Ubuntu + Fedora + Arch
- d) Flatpak/AppImage (univerzalni)

> [SCORING] a-> vsechny +0. b-> vsechny +1. c-> Qt +2, Go+htmx +2. d-> Flutter +1, Avalonia +1

**54. Auto-update mechanismus?**
- a) Kriticke — uzivatel NESMI mit starou verzi
- b) Dulezite, ale manualni update akceptovatelny
- c) Neni priorita

> [SCORING] a-> Avalonia +2 (NetSparkle/Squirrel), Qt +2. b-> vsechny +1. c-> Go+htmx +2

**55. Budete potrebovat embedded webview (pro phpMyAdmin, Mailhog apod.)?**
- a) Ano — chceme vse v jednom okne
- b) Externi prohlizec staci
- c) Nerozhoduje

> [SCORING] a-> Go+htmx +4 (uz je v browseru), Qt +2 (QWebEngineView). b-> vsechny +1. c-> vsechny +0

---

## SEKCE 6: Architekturni preference (vaha 10 %)

**56. Single binary nebo multi-process architektura?**
- a) Single binary (daemon + GUI = 1 EXE)
- b) Oddelene procesy (daemon + GUI komunikuji pres IPC)
- c) Nerozhoduje

> [SCORING] a-> Go+htmx +4 (prirozene — HTTP server = GUI). b-> Qt +2, Avalonia +2. c-> vsechny +0

**57. Preferovany IPC protokol s Go daemonem?**
- a) JSON-RPC pres named pipe (soucasny design)
- b) gRPC
- c) REST/HTTP
- d) stdin/stdout

> [SCORING] a-> Qt +2, Avalonia +2. b-> Avalonia +3 (GrpcDotNetNamedPipes), Qt +1. c-> Go+htmx +3 (nativni). d-> PySide +1

**58. Konfiguracni format?**
- a) TOML (soucasny design)
- b) YAML
- c) JSON
- d) Nezalezi — daemon to handluje

> [SCORING] d je spravna odpoved — GUI jen vola API. Vsechny +0.

**59. Potrebujete Command Palette (Ctrl+K)?**
- a) Must-have
- b) Nice-to-have
- c) Nepotrebujeme

> [SCORING] a-> Go+htmx +2 (Ninja Keys lib), Avalonia +2. b-> vsechny +1. c-> vsechny +0

**60. Drag-and-drop funkcionalita?**
- a) Ano — pretahovat projekty, soubory, menit poradi
- b) Minimalni (prehazovat sites v seznamu)
- c) Ne

> [SCORING] a-> Qt +3 (QDrag gold standard), Avalonia +2. b-> vsechny +1, Go+htmx +1. c-> vsechny +0

**61. Undo/redo pro konfiguracni zmeny?**
- a) Must-have
- b) Nice-to-have
- c) Nepotrebujeme — Git verzuje TOML

> [SCORING] a-> Qt +2 (QUndoStack), Avalonia +2 (ReactiveUI). b-> vsechny +1. c-> Go+htmx +1

**62. Klavesove zkratky a power-user funkce?**
- a) Kriticke — vim-level efficiency
- b) Zakladni zkratky (Ctrl+S, Ctrl+N)
- c) Mysi staci

> [SCORING] a-> Qt +3, Go+htmx +2 (browser shortcuts). b-> vsechny +1. c-> Flutter +1

**63. Settings sync mezi mashinami?**
- a) Planovane — chceme cloud sync
- b) Export/import staci
- c) Nepotrebujeme

> [SCORING] a-> Go+htmx +1, Avalonia +1. b-> vsechny +1. c-> vsechny +0

**64. Tabelarni prehled (DataGrid) pro sites, PHP verze, logy?**
- a) Plnohodnotny DataGrid (sort, filter, edit)
- b) Jednoduchy seznam staci
- c) Kartovy layout (cards, ne tabulka)

> [SCORING] a-> Avalonia +3 (DataGrid built-in), Qt +3 (QTableView). b-> vsechny +1. c-> Flutter +2, Go+htmx +2

**65. Potrebujete grafy / vizualizace (CPU, RAM, traffic)?**
- a) Ano — real-time dashboard
- b) Zakladni (sparklines)
- c) Ne

> [SCORING] a-> Qt +3 (Qt Charts), Go+htmx +2 (Chart.js). b-> vsechny +1. c-> vsechny +0

---

## SEKCE 7: Dealbreakery a priority (vaha 5 %)

**66. Co je VAS jediny nejdulezitejsi faktor?**
- a) Velikost bundle (maly installer)
- b) Rychlost vyvoje (time-to-market)
- c) Nativni vzhled (vypadni jako systemova app)
- d) Spotreba pameti
- e) Cross-platform konzistence
- f) Velikost ekosystemu a komunity

> [SCORING] a-> Go+htmx +3, Slint +2, Qt +2. b-> Go+htmx +3, PySide +2. c-> Qt +3, Avalonia +2. d-> Go+htmx +3, Qt +2, Slint +2. e-> Qt +2, Flutter +2, Go+htmx +2. f-> Qt +2, Flutter +2

**67. Co je absolutni dealbreaker?**
- a) Vyzadovany JavaScript/TypeScript
- b) Installer > 100 MB
- c) RAM > 200 MB
- d) Nutnost ucit se C++
- e) Nutnost ucit se Rust
- f) Browser-based UI (ne nativni okno)
- g) Licencni omezeni (ne-MIT)

> [SCORING] a-> vsechny +0 (zadny kandidat nepouziva JS). b-> eliminace Flutter (60+ MB), PySide (spatne). c-> eliminace PySide. d-> eliminace Qt. e-> eliminace Slint. f-> eliminace Go+htmx. g-> eliminace Qt (LGPL), PySide (LGPL)

**68. Kdybyste si museli vybrat DNES — intuice?**
- a) Qt6 C++/QML
- b) Avalonia UI
- c) Go + htmx + templ + systray
- d) PySide6 + Nuitka
- e) Flutter Desktop
- f) Slint

> [SCORING] Primo +5 bodu zvolenemu frameworku.

**69. Prijali byste browser-based UI, kdyby to znamenalo dodani o 2 mesice drive?**
- a) Ano — cas je priorita
- b) Mozna — ale nechci kompromis na UX
- c) Ne — nativni UI za kazdou cenu

> [SCORING] a-> Go+htmx +4. b-> Go+htmx +2. c-> Qt +3, Avalonia +3

**70. Seradte 1-6 (1 = nejdulezitejsi):**

| Faktor | Poradi (1-6) |
|--------|-------------|
| Velikost bundle | ___ |
| Spotreba RAM | ___ |
| Rychlost vyvoje | ___ |
| Nativni vzhled | ___ |
| Cross-platform | ___ |
| Ekosystem/komunita | ___ |

> [SCORING] Multiplikator na celou sekci: 1. misto = 3x, 2. = 2.5x, 3. = 2x, 4. = 1.5x, 5. = 1x, 6. = 0.5x

---

## VYHODNOCENI

### Krok 1: Sectete body po sekcich

| Sekce | Vaha | Qt6 | Avalonia | Go+htmx | PySide | Flutter | Slint |
|-------|------|-----|----------|---------|--------|---------|-------|
| 1. Tym a dovednosti | 20% | ___ | ___ | ___ | ___ | ___ | ___ |
| 2. Produktova vize | 15% | ___ | ___ | ___ | ___ | ___ | ___ |
| 3. Technicke pozadavky | 25% | ___ | ___ | ___ | ___ | ___ | ___ |
| 4. Vyvoj a udrzba | 15% | ___ | ___ | ___ | ___ | ___ | ___ |
| 5. Distribuce | 10% | ___ | ___ | ___ | ___ | ___ | ___ |
| 6. Architektura | 10% | ___ | ___ | ___ | ___ | ___ | ___ |
| 7. Dealbreakery | 5% | ___ | ___ | ___ | ___ | ___ | ___ |
| **VAZENY SOUCET** | 100% | **___** | **___** | **___** | **___** | **___** | **___** |

### Krok 2: Aplikujte multiplikatory z otazky 70

### Krok 3: Odectete eliminovane frameworky z otazky 67

### Krok 4: Vysledne poradi

| Poradi | Framework | Skore | Eliminovan? |
|--------|-----------|-------|-------------|
| 1. | _________ | _____ | Ne |
| 2. | _________ | _____ | Ne |
| 3. | _________ | _____ | Ne |
| 4. | _________ | _____ | ___ |
| 5. | _________ | _____ | ___ |
| 6. | _________ | _____ | ___ |

---

## Rychla reference — co ktery framework prinasi

| Framework | Nejlepsi pro | Nejhorsi pro |
|-----------|-------------|-------------|
| **Qt6 C++/QML** | Nativni feel, tray, performance, zpetna kompatibilita | Rychlost vyvoje, C++ learning curve |
| **Avalonia UI** | .NET tym, MVVM, DataGrid, enterprise, MIT licence | Bundle size (34 MB), mene zraly nez Qt |
| **Go+htmx+templ** | Solo vyvojar, rychlost, Go-only stack, single binary | Neni nativni okno, offline webview problemy |
| **PySide6+Nuitka** | Python tym, rychly prototyping, Qt pod kapotou | RAM (80-120 MB), Nuitka compile casy |
| **Flutter Desktop** | Mobilni budoucnost, hot reload, moderni UI | Tray (community plugin), xterm (stale) |
| **Slint** | Rust tym, nejmensi binary (3 MB), embedded-ready | Zadny system tray, maly ekosystem |

---

*Dokument vygenerovan 2026-04-09 pro projekt DevForge.*
*Pouziti: Vyplnte, sectete, rozhodnete. Jeden vecer prace = mesice jistoty.*
