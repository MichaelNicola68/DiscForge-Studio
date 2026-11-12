DiscForge Studio 1.5.0-alpha.2-dev.9.1

UTF-8-Korrektur
- Fehlerhaft dargestellte Umlaute und Sonderzeichen in der Benutzeroberfläche korrigiert.
- Beschriftungen, Statusmeldungen, Brennprotokoll und Fehlertexte bereinigt.
- Funktionsumfang von dev.9 bleibt unverändert.

Test:
1. dotnet clean
2. dotnet build -c Release
3. dotnet run -c Release
4. Startseite, Datei-Zusammenstellung und Brennen & Verify+ auf korrekte Umlaute prüfen.
