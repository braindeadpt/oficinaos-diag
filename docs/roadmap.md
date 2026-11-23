# Roadmap notas — oficinaos-diag

Notas de investigação para features futuras. Não são compromissos — são
decisões já estudadas para não ter de se re-avaliar.

## scrcpy (espelho de ecrã Android)

**Decisão: licença OK para uso comercial.** scrcpy (Genymobile / Romain Vimont)
é **Apache License 2.0** — permite uso comercial, redistribuição e bundling
num produto pago. Repo: `github.com/Genymobile/scrcpy`.

Obrigações se for bundled:

- Distribuir uma cópia do `LICENSE` Apache-2.0 junto dos binários scrcpy
  (ex.: `tools/scrcpy/LICENSE`).
- Manter copyright notices do upstream; se algum ficheiro for modificado,
  marcar com nota de alteração proeminente.
- Sem `NOTICE` file no upstream, nada mais é exigido. Sem copyleft — não obriga
  a abrir o nosso código.
- Reutiliza o `adb` já bundled (`tools/platform-tools`) — scrcpy precisa dele
  no PATH ou ao lado.

**Uso planeado:** botão "Espelhar ecrã" na app — lança `scrcpy.exe -s <serial>`
numa janela para o técnico operar o telefone (útil para guiar o cliente,
testar apps, confirmar a funcionalidade pós-reparação). Feature **Pro**
(`diag` module) — bundling aumenta o zip em ~30 MB; fica para mais tarde,
após validar procura nas lojas beta.

## Auto-orçamento (catálogo determinístico)

**Não usar IA para preços** — a IA interpreta o diagnóstico; o preço vem de
regras determinísticas do catálogo da loja.

Desenho proposto:

1. **Input**: modelo do dispositivo (resolvido via `MarketingName` + código) +
   checks com falha/aviso do scan (`battery.capacity < 80%`, `test.touch fail`,
   `security.bootloader warn`, …).
2. **Match**: mapear modelo → entrada no catálogo de dispositivos da app
   (`devices` table + repair catalog) — o match exacto usa o código (SM-S918B),
   o nome comercial é display.
3. **Regras**: cada `check com fail/warn` mapeia para serviços candidatos
   (`battery.*` → troca de bateria; `test.touch` → módulo ecrã;
   `logs.crashes sys` → diagnóstico software). Preço = catálogo da loja
   (peça em stock + mão de obra configurada), nunca gerado.
4. **Output**: pré-orçamento "sugerido" no intake-request da loja — o técnico
   confirma/ajusta antes de virar orçamento real. A sugestão mostra *porquê*
   (o check que a gerou), não um número nu.
5. **Sem catálogo/sem match** → sem sugestão. Falha silenciosa > preço errado.

Fases:

- F1: scan → checks já chegam à loja via `diag-intake` (implementado)
- F2: match modelo→dispositivo no catálogo + tabela `check → serviço`
- F3: sugestão de linhas de reparação no detalhe do pedido
- F4: ajustes por loja (multiplicador, descontos de campanha) — **opcional**

Dependências: catálogo de reparações por modelo populado na app da loja
(já existe parcialmente — repairs/services por device). Re-avaliar quando
houver dados reais de scans beta.

## Plano Diag Pro — evolução manutenção (TASK-90)

Estado: P0 (relatório IA manutenção #45/#59, gestor apps #46, limpeza
medida #47, auditoria segurança #48) **feito**. Este plano cobre o resto.

### Fase A — estado do dispositivo e score (quick wins)

1. **Deteção de modos de serviço/flash** — Android: `adb devices`/`getprop`
   para fastboot/recovery/EDL/download mode + props; iOS: distinguir
   normal/DFU/recovery via estado libimobiledevice. Alerta no relatório
   antes de scans que falhariam silenciosamente.
2. **Health score 0–100** — índice combinando bateria, armazenamento,
   segurança (root/MDM/contas), crashes, temperatura e teste on-device.
   Evolução antes/depois no histórico + export no relatório. Marketing/UX,
   não substitui grading A–D. **Feito** — `OficinaDiag.Health.HealthScore`
   no Core (6 categorias com pesos, dados em falta redistribuem o peso),
   etiqueta na janela, checks `health.*` no relatório e score na lista
   antes/depois. Follow-up possível: sparkline de score no histórico.

### Fase B — teste on-device avançado

3. **Novos checks no TestServer/wwwroot** — `test.camera_front`,
   `test.camera_back` (getUserMedia), `test.gpu` (WebGL stress),
   `test.gps` (geolocation+precisão), `test.brightness`, touch sampling
   rate se exposto. `skipped` quando a API não existe — nunca `fail` por
   falta de permissão (Safari iOS é mais restrito). **Feito** — fase
   «testes avançados» na página on-device (câmara com confirmação visual,
   stress WebGL ~3 s, GPS com timeout, brilho e taxa de toque por
   getCoalescedEvents); `test.*` novos alimentam a categoria «teste no
   aparelho» do health score.

### Fase C — perfil de desempenho/bateria

4. **Stress leve 5–10 min** durante scan manutenção: WebGL/canvas com
   temperatura CPU/SoC vs célula, quedas USB; repetir pós-otimização.
   Prova mensurável de autonomia ao cliente (usa PowerSession + medições
   da limpeza #47). **Feito** — janela «Perfil de desempenho» dedicada
   (botão na vista principal): `perf-test.html` WebGL/canvas na página
   do aparelho, janela de host ~5 min por defeito (5/7/10 min,
   configurável), telemetria SCAN+ (corrente/tensão, térmico célula vs
   SoC, drops USB, frequência CPU Android). Checks `perf.*` entram no
   relatório e na comparação antes/depois; métricas não expostas pelo
   dispositivo ficam `skipped`, nunca `fail`.

### Fase D — iOS

5. **Inventário iOS via installation_proxy** — `ideviceinstaller list`
   (CFBundleIdentifier, versões, Static/DynamicDiskUsage) = "o que ocupa
   espaço" no iPhone sem jailbreak. Desinstalação de apps de utilizador
   como Pro com confirmação. `security.mdm`/`findmy` com domínios e
   instruções guiadas. **Feito** — caminho escolhido: o
   `ideviceinstaller.exe` já bundled pelo pacote iMobileDevice-net
   (`-l -o list_all,xml`), não a API gerida — parsing do plist XML fica
   isolado e testável, sem nova dependência. Checks `ios.apps.count`,
   `ios.apps.space` (Static+Dynamic) e `ios.apps.top`; serviço em falta
   ou iOS antigo → `skipped` honesto. Gestor de apps iOS no botão Apps:
   inventário + desinstalação de apps de utilizador com confirmação e
   verificação por relista (o exit code do tool não é fiável); sem
   disable/enable — o iOS não expõe isso por esta via. `security.mdm`
   passa a nomear os domínios lockdown consultados e `findmy` ganha o
   caminho guiado (Find My iPhone / iCloud.com); continua `skipped`
   porque nenhum domínio o expõe.

### Fase E — operador/UX Pro

6. **Templates de serviço** — troca bateria, troca ecrã, compra usado,
   otimização, migração WhatsApp: checklist específico + medições
   obrigatórias + relatório IA diferente por tipo.
7. **Personalização assistida** — Android: intents para definições
   (wallpaper/ecrã/bateria/apps), animações/densidade/fontes onde o OEM
   permite, sugestão launchers/icon packs. iOS: assistente visual com QR
   para o cliente seguir no próprio iPhone.
8. **Batch/multi-device** — bancadas com N telefones, etiquetas em lote,
   comparação de scans do mesmo modelo, fila "aparelhos por fazer".

### Fase F — integrações

9. **Auto-orçamento + peças** (desenho acima) — F2–F3 do roadmap:
   match modelo→catálogo, checks→serviços candidatos, integração
   `market-prices` para peças e `diag-intake` para pré-orçamento.
10. **scrcpy** — bundling ~30 MB, `tools/platform-tools` já existe, Pro
    (`diag` module). Licença Apache-2.0 OK (nota acima).

### Bloqueado — precisa de hardware real

11. **wa-migrate UI Pro** — core library existe (`WaStore`, `WaIosReader`,
    `WaHtmlExport`, `WaContacts`) sem UI. Faltam os gates C (restore
    domain-scoped) e D (WhatsApp não apaga BD injetada) validados em
    dispositivos reais antes de prometer a feature.
