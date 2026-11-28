# Pipeline diag + reparação — arquitetura UX, fontes de dados e divisão free/Pro

Nota de arquitetura decidida em sessão com o dono (2026-11). Documento de
referência para as implementações — quando uma decisão aqui mudar, edita-se
o documento, não se deixa a divergir.

## 1. Visão e perímetro

O Oficina Diag é uma ferramenta de **diagnóstico profundo e reparação de
software legítima** para lojas de reparação. Não é — e nunca será — uma
«box» de hack: o mercado já está cheio delas e não é esse o produto.

**Dentro do perímetro:**
- diagnóstico profundo (ler tudo o que o hardware reporta, interpretar)
- restauro de firmware stock / unbrick (MediaTek, Samsung, Qualcomm, iOS)
- wipe certificado com prova RGPD para compra de usados / refurb
- deteção de tampering como *diagnóstico* (Knox/KG state, activation
  state, sinais de peças não-originais) — informação para a loja decidir

**Fora do perímetro, sempre** (nem como «experimento», nem escondido):
- FRP bypass, iCloud bypass, MDM removal
- IMEI repair/change — em Portugal é território criminal (receptação)
- carrier unlock, patching de segurança para contornar proteções

A linha: **repõe o aparelho de fábrica sim; contorna proteções não.**

## 2. UX — o pipeline da bancada

### O problema

A janela principal cresceu como catálogo de features: uma parede de
botões (Scan, Test, Bench, Perf, Mirror, Apps, Maintenance, Checklist,
Service, Compare, Label, Client, Export, Send, AI…), cada um a abrir uma
janela solta cujos resultados se perdem ao fechar. Adicionar diag
profundo + reparação neste modelo rebenta a usabilidade.

### O modelo: organizar pelo trabalho, não pela feature

Ninguém na loja pensa «quero abrir a feature X» — pensa *chegou um
telefone → o que tem? → arranjo → confirmo → entrego*. A UI passa a ser
esse pipeline:

```
┌──────────────────────────────────────────────────────────┐
│ [aparelho ligado: Xiaomi Note 12 · Android 14 · bat 87%] │
│                                                          │
│  ① DIAGNOSTICAR   ② REPARAR   ③ VERIFICAR   ④ ENTREGAR   │
└──────────────────────────────────────────────────────────┘
```

**① Diagnosticar** — tudo *read-only*; impossível estragar algo.
Scan rápido, leituras profundas (bateria/ciclos, eMMC/UFS wear,
térmicas, panic logs iOS), testes interativos (Test/Bench/Perf/Mirror
como subsecções). Tudo o que se mede fica registado **na sessão do
aparelho** — não em janelas descartáveis.

**② Reparar** — tudo o que *escreve* no aparelho. Restauro de firmware,
wipe certificado, iOS restore. Visualmente distinto, confirmações
explícitas, log de cada operação. Regra de ouro: **se não escreve no
telefone não vive aqui — e vice-versa.** É esta a linha que elimina a
confusão diagnóstico/reparação.

**③ Verificar** — re-scan pós-reparação + compare antes/depois como
cidadão de primeira classe: é a prova do trabalho.

**④ Entregar** — relatório, etiqueta, envio cliente/Cloud. Bónus
estrutural: **o relatório escreve-se sozinho**, porque as etapas 1–3 já
registaram tudo na sessão.

### O modo do aparelho governa a UI

O aparelho só está num estado de cada vez; cada estado tem um conjunto
pequeno de ações válidas. A UI mostra **só** o que se aplica:

| Estado do aparelho | A UI mostra |
|---|---|
| Android normal (adb) | diagnóstico completo + testes |
| iPhone normal (usbmux) | diagnóstico iOS + panic logs |
| fastboot / recovery | diagnóstico limitado + reparação |
| BROM / preloader (MTK) | só reparação de firmware |
| EDL 9008 (Qualcomm) | só reparação de firmware |
| download mode (Samsung) | só reparação de firmware |
| DFU / recovery (iOS) | só restore |
| desligado / brickado | guia para entrar no modo de recuperação |

Esconder o não-aplicável elimina metade do ruído sem tirar poder nenhum.
(Continua o trabalho já iniciado na Fase A.1 do roadmap — deteção de
modos.)

### O que se mantém

O visual terminal retro — é a marca. E o contexto de **um aparelho de
cada vez na bancada** (multi-device fica para a Fase E.8 do roadmap; o
pipeline aguenta, mas o topo da janela muda).

## 3. Divisão free/Pro — decidido

Filosofia mantida: **grátis = valor local genuíno; Pro = inteligência,
integração e responsabilidade profissional.** O princípio novo, usado
por todo o mercado: **«ver é grátis, mexer é pago».**

| Etapa | Grátis | Pro |
|---|---|---|
| ① Diagnosticar | scan como hoje + **leituras profundas em bruto**: ciclos de bateria, eMMC wear %, térmicas, panic logs exportáveis | **interpretação**: panic log decodificado («componente X a falhar»), veredito de saúde com explicação — a mesma divisão do AI report: dados grátis, inteligência paga |
| ② Reparar | nada | **tudo** — restauro de firmware, wipe certificado, iOS restore |
| ③ Verificar | re-scan + compare | — |
| ④ Entregar | exportar HTML/JSON | relatório IA + **certificado de apagamento RGPD** (nasce da operação de wipe do ②) |

Decisões registadas:

1. **Reparação dentro do pacote Pro existente** — não há entitlement novo
   (`diag-repair` rejeitado); afeta preço e pitch: o Pro passa a ser «a
   ferramenta completa da bancada»
2. **Wipe certificado**: a operação é Pro no ②; o certificado é um
   documento que nasce no ④ — contam juntos, não se vendem separados
3. **Leituras em bruto grátis, interpretação Pro** — incluindo panic
   logs (exportar grátis, decodificar Pro)

Enforcement: como já está decidido no roadmap (Fase F.10), **o Pro é
enforced pela Cloud** — endpoint de entitlement, nunca flag/licença
local. As operações de reparação são locais mas o gate consulta a Cloud.
Nota honesta já assumida no ecossistema: o diag é MIT, quem compilar o
fonte salta o gate — o fosso real é Cloud + IA + integração + confiança,
não o bit que liga o flash.

A subtileza que protege o Pro sem aleijar o grátis: o grátis mostra
«eMMC life: 85% consumida» sem dizer o que significa. O técnico aprende
a confiar na ferramenta; quando aparece o caso complicado, o veredito
interpretado é a razão natural de pagar. **O grátis é bom o suficiente
para viciar, nunca para concluir o raciocínio.**

## 4. Fontes de dados por plataforma

### Android (via adb — já embutido, zero ferramentas novas)

| Dado | Fonte |
|---|---|
| bateria: corrente real, voltagem, ciclos | `/sys/class/power_supply/`, `dumpsys battery`/`batterystats` |
| desgaste eMMC/UFS (% vida consumida) | `/sys/class/block/mmcblk0/device/life_time` (muitas marcas) |
| crashes/reinícios | tombstones + logcat (kernel panic = indício de hardware — um erro de kernel/driver também causa panic; app crash = software) |
| zonas térmicas | `/sys/class/thermal/` |
| sensores | `dumpsys sensorservice` |
| modem/sinal/SIM | `dumpsys telephony.registry` |

Nota: telefone lento/com crashes aleatórios é muitas vezes flash a
morrer — eMMC wear é dos testes com mais valor e ninguém o faz.

### iPhone (pymobiledevice3 — salto sobre os idevice* atuais)

- **panic-full logs**: o iOS regista qual componente falha (NAND,
  sensor, CPU, PMIC) — o iPhone que reinicia de 3 em 3 minutos tem a
  resposta escrita no log. É a ferramenta secreta das lojas pro
- bateria: ciclos exatos, capacidade design vs. atual, temperatura,
  amperagem (IORegistry)
- MobileGestalt: serials, modelo, activation state
- restore/update de iOS legítimo (equivalente ao iTunes restore)
- corre em Windows; verificar licença antes de embutir

### Modos mortos (reparação — etapa ②)

| Ferramenta | Chipset | Licença | Notas |
|---|---|---|---|
| **bkerler/mtkclient** | MediaTek BROM/preloader | GPLv3 | Python; usa-se só a parte de flash/backup stock — a UI nunca diz «bootrom exploit», diz «restauro de firmware — MediaTek» |
| **Benjamin-Dobell/Heimdall** | Samsung download mode | **MIT** ✅ | encaixe legal perfeito; nativo Windows |
| **qualcomm/qdl** | Qualcomm EDL | **BSD-3** ✅ | oficial da Qualcomm; Windows pela COM do driver QDLoader 9008, sem Zadig |

**Excluído: bkerler/edl** — apesar de tecnicamente superior em Qualcomm,
tem restrição extra à GPLv3 («no use in commercial products without
prior permit») incompatível com o ecossistema. `qdl` cobre o essencial.

**Nunca entram**: Tunlocker/FlashPilot e família — FRP, IMEI, MDM.
Território criminal em PT e mancha o produto.

## 5. Riscos de integração (por ordem de dor)

1. **Antivírus/SmartScreen** — mtkclient funciona à base de exploits de
   bootrom; AVs classificam-no como hacking tool. O diag já luta com
   SmartScreen por não ser assinado. **Sequência obrigatória: SignPath
   primeiro, mtkclient depois** — embutir exploit-tooling antes da
   assinatura é pedir flag vermelho em metade dos Windows.
2. **Drivers Windows** — mtkclient precisa de usbdk + MTK port; Heimdall
   precisa de WinUSB via Zadig. Instalações com admin que a loja
   não-técnica não sabe fazer: o setup tem de automatizar/verificar,
   como já faz com o adb.
3. **Python embutido** — mtkclient e pymobiledevice3 são Python; o diag
   hoje só embute binários nativos. Opções: PyInstaller one-file no
   build (~30–50 MB cada) ou reimplementar em C# só o essencial —
   para *deteção* de modos basta ler VID/PID USB, não é preciso Python
   nenhum.

## 6. Inventário atual → destino no pipeline

| Superfície atual | Aterra em |
|---|---|
| Scan | ① Diagnosticar (núcleo) |
| Test (ecrã/toque on-device) | ① — testes interativos |
| Bench, Perf | ① — testes interativos |
| Mirror (scrcpy) | ① — inspeção/verificação visual |
| Apps (gestor Android/iOS) | ① — leitura; a desinstalação (escreve!) move-se para ② |
| Maintenance (limpeza medida) | **② Reparar** — executa ações no aparelho |
| Checklist, Service (templates) | fluxo de intake — envolve as etapas todas |
| Compare | ③ Verificar |
| Export (HTML/JSON) | ④ Entregar — grátis |
| Send / Ai (relatório IA, cloud) | ④ Entregar — Pro |
| Label, Client | ④ Entregar |
| Insurance, SendDialog, TokenDialog | ④ / configuração |

As janelas soltas (BenchWindow, PerfWindow, CompareWindow…) passam a
painéis dentro das etapas — é um refactor estrutural da MainWindow, das
maiores mexidas na UI desde o início. Alternativa barata rejeitada:
agrupar botões em 4 grupos rotulados sem mudar janelas — compra
legibilidade mas a confusão estrutural continua.

## 7. Sequência de implementação proposta

1. **Deteção de modos** (VID/PID USB + estados adb/usbmux) — barato,
   zero risco, já na Fase A.1 do roadmap; é o enabler do «modo governa
   a UI»
2. **Leituras profundas Android** (eMMC wear, térmicas, power_supply) —
   grátis, valor imediato, zero ferramentas novas
3. **pymobiledevice3** — panic logs + bateria iOS (verificar licença
   primeiro); interpretação Pro ligada ao AI report
4. **Refactor do pipeline UX** — com as etapas já povoadas
5. **SignPath** concluído → **mtkclient/Heimdall/qdl** na etapa ② —
   primeiro deteção + backup, flash/write só depois de maduro
6. **Wipe certificado** — operação Pro + documento RGPD no ④
