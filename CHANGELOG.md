# Changelog

Formato: [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).
Versões seguem o tag `v*` do release.

## Unreleased

- WhatsApp: primeiro incremento do módulo de migração/resgate
  (`OficinaDiag.WhatsApp`) — inspetor read-only que deteta a variante da BD
  (ChatStorage iOS vs msgstore Android), versão do modelo Core Data,
  integridade e contagens; leitor streaming de `ChatStorage.sqlite` e export
  para HTML legível (um ficheiro por conversa + índice). Provado contra uma
  BD real de WhatsApp Business (1.43M mensagens / 2567 conversas).
  Sem UI ainda — o wiring Pro/UI e a conversão são fases seguintes.
- Release: o pacote win-x64 deixa de sair incompleto em silêncio — o workflow
  falha antes do draft se o zip não existir, não abrir, ou se faltar
  `OficinaDiag.exe`, `tools/platform-tools/adb.exe`, `Data/models.json` ou
  `Tests/wwwroot/device-test.html`.
- Updater: os ficheiros do pacote (exe, `adb`, `models.json`, página do teste)
  vêm de `PackagePaths` (verificado contra a lista do pacote e o `AssemblyName`
  do csproj) em vez de caminhos escritos à mão.
