# pymobiledevice3 no Diag — como embutir cumprindo a GPL (nota de decisão)

Documento de análise para o mentor. **Sem código nesta fase** — só a decisão
informada. Escrito a 2026-11-28 sobre pymobiledevice3 **11.66.1** (PyPI,
licença declarada `GPL-3.0-or-later`).

## 1. O problema jurídico em uma frase

O Diag é MIT e distribuído como executável único. pymobiledevice3 é GPL-3.0:
qualquer programa que o **incorpore** (importar a biblioteca, correr no mesmo
processo, ou distribuir um bundle em que ele seja parte funcional do produto)
passa a ser, nessa distribuição, obra licenciada sob GPL — incluindo o nosso
código. A forma padrão da indústria de usar ferramentas GPL dentro de produtos
com outra licença é a **agregação**: processo separado, comunicação por linha
de comandos/pipes, sem partilha de espaço de memória nem dependência funcional
forte. É o mesmo modelo que já usamos com adb e libimobiledevice (este último
LGPL/GPL já hoje, aliás).

## 2. A arquitetura que cumpre

```
OficinaDiag.exe (MIT)                — nunca importa nada GPL em-proc
    │  Process.Start, stdout/stderr
    ▼
pmd3/  (diretoria separada no instalador)
    ├── pymobiledevice3.exe          — bundle PyInstaller do projeto
    │                                  original, inalterado
    ├── LICENSE.GPL-3.0.txt          — texto integral da licença
    └── OFERTA-CODIGO-FONTE.txt      — «o código-fonte deste componente
                                        está em github.com/doronz88/pymobiledevice3
                                        (tag exata) ou disponível por escrito
                                        durante 3 anos»
```

Regras que a mantêm GPL-compliant:

1. **Processo separado, comunicação por CLI/stdout.** O Diag invoca
   `pymobiledevice3.exe <subcomando>` e lê a saída — exatamente o que a GPL FAQ
   da FSF descreve como «at arm's length»: dois programas a comunicar por
   pipes/sockets são obras separadas. O MIT do Diag não é contaminado.
2. **O bundle é o projeto original, não um fork.** Se algum dia tivermos de
   o alterar, as alterações publicam-se (são GPL por natureza).
3. **Licença + oferta de fonte no instalador**, ao lado do binário, e menção
   no ecrã «Sobre»/licenças de terceiros. GPL-3.0 §6 exige «Corresponding
   Source» ou oferta escrita válida 3 anos — a nota acima cobre-o, e convém
   espelhar o tarball da tag exata num release asset nosso para não depender
   do upstream.
4. **Instalador**: o componente é opcional e identificado («diagnóstico iOS
   avançado — software GPL-3.0 de terceiros»). Não é escondido.

## 3. O custo real — tamanho do bundle

pymobiledevice3 11.66.1 declara `requires-python >=3.9` e **53 dependências
diretas**, várias pesadas:

| dependência | para que serve no pmd3 | peso típico em PyInstaller |
|---|---|---|
| `av` (PyAV) | vídeo/screen mirroring | ~40–80 MB (traz ffmpeg) |
| `cryptography` | TLS/SSL dos serviços Apple | ~10 MB |
| `fastapi`+`uvicorn`+`starlette` | webremote/lockdown tunneling | ~15 MB |
| `Pillow` | screenshots | ~5 MB |
| `construct`, `pyusb`, `qh3`, … | protocolos | pequeno-médio |

Estimativa realista de `pyinstaller --onefile`: **100–160 MB comprimido**,
que dentro do nosso instalador (hoje ~40-60 MB) mais do que duplica o tamanho.
Mitigações possíveis, em ordem de esforço:

1. `--onedir` em vez de `--onefile` (arranque mais rápido, mesmo tamanho em disco);
2. cortar subcomandos que não usamos (mirroring/webremote) — mas as dependências
   são declaradas ao nível do pacote, e removê-las implica **fork** (= passa a
   ser código nosso sob GPL, manutenção nossa);
3. usar só `pip install --no-deps` + cherry-pick — frágil, mesma consequência.

**Conclusão de tamanho**: aceitável como componente opcional de download
separado (feature-on-demand), mau como payload obrigatório do instalador.

## 4. Windows sem drivers extra? — Não

Este é o ponto mais importante para a decisão. O nosso requisito de base do
pipeline é «sem drivers novos». Factos:

- pymobiledevice3 fala com o iPhone através do serviço **usbmuxd**. Em Windows
  esse serviço **não existe no sistema operativo** — vem com o iTunes clássico
  ou com a app **Apple Devices** da Microsoft Store (o caminho moderno da Apple
  desde 2024).
- O próprio README do projeto diz que em Windows é preciso ter o iTunes/Apple
  Devices instalado (ou correr um usbmuxd alternativo, que é exatamente o tipo
  de driver novo que o plano proíbe).
- Ou seja: **em Windows, pymobiledevice3 só funciona em máquinas onde o dono do
  iPhone já instalou software Apple** — ou nas nossas bancadas, se aceitarmos
  instalar a app Apple Devices nelas (não é driver «nosso», é software de
  sistema de terceiros, tal como hoje dependemos do que o libimobiledevice
  já traz — mas atenção: libimobiledevice em Windows já exigiu precisamente
  esse mesmo ecossistema).

Consequência prática: para a bancada da oficina, o caminho realista é
**instalar a app Apple Devices uma vez na máquina** e documentá-lo — não é
zero-fricção, mas é uma decisão de setup da bancada, não do cliente.

## 5. O que nos compra (e o que já temos sem ele)

Já distribuímos **libimobiledevice** (ideviceinfo, idevicebackup2, etc.) e o
`IosCollector` atual cobre identificação, bateria básica e estado. O que o
pymobiledevice3 acrescenta de relevante para o pipeline:

- **syslog/crash reports**: listar e extrair `com.apple.crashreport` — panics,
  resets — diretamente do aparelho (sem isto, leituras de crash iOS ficam
  dependentes do pairing e de AFC parcial, que o libimobiledevice já oferece
  em parte);
- **diagnostics relays** (mobileactivation, misagent, lockdown completo);
- perfis, provisionamento, springboard — quase tudo fora do nosso âmbito.

Honestamente: **boa parte do valor (panic logs) já é alcançável com o
libimobiledevice que embutimos** (`idevicecrashreport`/`afc` lêem
`/var/mobile/Library/Logs/CrashReporter` após pairing). A diferença real do
pmd3 é o acesso mais completo aos serviços lockdown modernos (iOS 17+ mudou o
emparelhamento e o libimobiledevice tem atrasos nesse suporte — o pmd3,
mantido ativamente — 11.x em 2025 — cobre os protocolos novos).

## 6. Recomendação

1. **Não embutir já.** Fase 1: implementar primeiro o que o libimobiledevice
   existente já permite (panic/crash logs via AFC + syslog relay), com os
   mesmos estados «não disponível/requer pairing». Custo: nenhum licenciamento
   novo, nenhum MB novo.
2. **Adiar o pymobiledevice3 para quando iOS 17+ o exigir de facto** (pareamento
   moderno a falhar no libimobiledevice nas bancadas reais — a medir com
   aparelhos reais, não agora).
3. Quando entrar, entrar como **componente GPL separado, opcional, de download
   sob demanda** (secção 2), com nota no instalador e oferta de fonte — nunca
   importado em-processo nem compilado para dentro do executável principal.
4. Em Windows, documentar o pré-requisito **app Apple Devices** na máquina da
   bancada (uma vez, por bancada), porque «sem drivers extra» não é
   alcançável para iOS moderno — nem com pmd3 nem sem ele.

## 7. Pergunta que fica para o mentor

A bancada-alvo costuma ter iTunes/Apple Devices instalado? Se sim, o custo do
pré-requisito é nulo e o caminho do pmd3 fica mais barato; se a política for
bancadas limpas, o libimobiledevice (que já controlamos) é o único caminho
coerente até segunda ordem.
