# Changelog

Formato: [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).
Versões seguem o tag `v*` do release.

## Unreleased

- Saúde do aparelho: novo **health score 0–100** (`OficinaDiag.Health`) —
  índice de vitrine que soma bateria, armazenamento, segurança,
  estabilidade, temperatura e teste no aparelho. Não substitui o grau A–D:
  checks ausentes/skipped saem do cálculo (o peso redistribui, logo um
  iPhone com menos dados visíveis não é penalizado). Aparece na janela
  (percentagem + etiqueta Saudável/Razoável/Preocupante), no relatório
  HTML/JSON como checks `health.*` com breakdown por categoria, na vista
  cliente e na etiqueta antes/depois do comparar scans. Fase A.2 do plano
  Diag Pro.
- Relatórios: o HTML exportado segue agora o tema escolhido em Opções —
  Moderno (claro, Segoe UI, acentos azuis), Terminal (verde-fósforo) ou
  Windows 95 (claro, Tahoma). Aplica-se ao relatório normal e ao relatório
  IA; os relatórios de cliente e seguradora mantêm o layout próprio.
- Tema: **Moderno é o novo defeito** numa instalação limpa (antes:
  Terminal). A escolha guardada continua a ser respeitada.
- Scan: a janela deixava de responder durante a coleta — os `WaitForExit`
  síncronos dos coletores corriam na UI thread; a coleta passou para o
  thread-pool.
- Relatório IA: os logs do scan passam a ir limitados (80 linhas/14KB
  Android, 14KB iOS) — a Groq devolvia 413 (tokens/pedido) e o relatório
  falhava com 502.
- Emparelhamento: "guardar neste PC" vem ligado por defeito no diálogo do
  código — sem isto o token só vivia em memória e o código (uso único) não
  o repunha ao fechar a app.
- Scan USB: o detetor Android **nunca viu um device em Windows** — o
  `adb.exe` emite CRLF e o regex `(\w+)$` multiline não casa (`$` ancora
  antes do `\n`). Fix `\r?` + `ParseAdbDevices` extraído e provado com
  bytes reais do adb (CRLF e LF).
- Release/dev: o iMobileDevice-net copia natives de ~21 RIDs para o
  output; o novo target `StripForeignNativeRuntimes` deixa só `win-*` —
  menos ~20 dirs de peso morto e fim dos alertas Defender
  `Exploit:MacOS/LimeRain` (libirecovery macOS) nas máquinas dos clientes.
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
