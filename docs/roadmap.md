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
