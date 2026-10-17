# ReadPathParity

Narzędzie deweloperskie do porównywania pełnych odpowiedzi usług na tych samych danych oraz pomiarów ścieżki surowej i pakietów wejściowych. Instrukcja uruchomienia, warianty capture/replay i ograniczenia pomiarów są w [PERFORMANCE.md](../../docs/PERFORMANCE.md).

Zwykły probe dopuszcza GET i `get_*` RPC. `--prepare` jest jawną operacją zapisu service role — nie używaj go do testu tylko do odczytu. Sekrety pochodzą z konfiguracji API, nie z fixture. Snapshoty i wyniki przechowuj w ignorowanym katalogu. Starszy tryb `--stages` ma znany problem niejednoznacznej refleksji; do weryfikacji używaj `--parity`.
