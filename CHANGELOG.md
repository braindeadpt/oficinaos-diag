# Changelog

Formato: [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).
Versões seguem o tag `v*` do release.

## Unreleased

- Release: o pacote win-x64 deixa de sair incompleto em silêncio — o workflow
  falha antes do draft se o zip não existir, não abrir, ou se faltar
  `OficinaDiag.exe`, `tools/platform-tools/adb.exe`, `Data/models.json` ou
  `Tests/wwwroot/device-test.html`.
- Updater: o nome do executável deixa de estar escrito à mão no script de
  atualização; vem de `UpdateChecker.AppExeName`, verificado por teste contra
  o `AssemblyName` do csproj, para renomear a app não partir o updater em
  silêncio.
