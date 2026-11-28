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
   `pymobiledevice3.exe <subcomando>` e lê a saída. Atenção à nuance que a
   primeira versão desta nota simplificou de mais: a GPLv3 §5 distingue obras
   independentes pelo **grau de integração efetivo**, e processos separados com
   CLI *favorecem* a independência mas não a *demonstram* automaticamente. O
   que a sustenta aqui é a comunicação ser genuinamente «at arm's length»:
   saídas genéricas de texto/JSON, sem estruturas de dados partilhadas, sem
   dependência de formato interno — o Diag tem de conseguir viver com qualquer
   outra implementação do mesmo comando. **A conclusão «o MIT não é
   contaminado» fica condicionada a manter essa disciplina** — se um dia a
   integração apertar (parsers profundos de formatos internos do pmd3,
   coordenação íntima de estado), a análise tem de ser refeita.
2. **O bundle é o projeto original, não um fork.** Se algum dia tivermos de
   o alterar, as alterações publicam-se (são GPL por natureza).
3. **Fonte correspondente por download, não oferta escrita.** A primeira
   versão desta nota propunha a oferta escrita de 3 anos — errado para o nosso
   caso: a GPLv3 §6(b) reserva essa via à distribuição **física**. Para
   distribuição por download, a §6(d) exige acesso equivalente à fonte
   correspondente **pelo mesmo meio e sem custo adicional**. Na prática:
   espelhamos o tarball da tag exata (e os nossos scripts de empacotamento
   PyInstaller, que também fazem parte da «Corresponding Source» do bundle)
   como release asset no mesmo sítio onde o instalador se descarrega, e o
   ficheiro `OFERTA-CODIGO-FONTE.txt` aponta para esse download. A tag
   upstream sozinha não chega — não cobre os scripts de empacotamento e
   podemos ficar dependentes de terceiros.
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

Já distribuímos **libimobiledevice** e o `IosCollector` atual cobre
identificação, bateria básica e estado — **e já recolhe relatórios de crash e
panic** (`CollectCrashLogs`, com `idevicecrashreport -e -k`, sem apagar nada
do telefone). A primeira versão desta nota apresentava os panic logs como
valor novo do pmd3 — errado: esse trabalho já existe. O que o pymobiledevice3
acrescentaria de facto:

- **syslog em streaming** e leituras de diagnóstico mais profundas;
- **diagnostics relays** (mobileactivation, misagent, lockdown completo);
- **suporte do emparelhamento moderno do iOS 17+** — o libimobiledevice tem
  atrasos conhecidos nesse suporte e o pmd3 (mantido ativamente, 11.x em
  2025) cobre os protocolos novos. Este é, em rigor, o único argumento de
  peso;
- perfis, provisionamento, springboard — quase tudo fora do nosso âmbito.

## 6. Recomendação

1. **Não embutir já.** O que o libimobiledevice existente já permite —
   incluindo os crash/panic logs, que já estão implementados — cobre a
   necessidade atual. O trabalho útil imediato é validar essa cobertura em
   aparelhos reais e acrescentar syslog se fizer falta, não trocar de motor.
2. **Adiar o pymobiledevice3 para quando iOS 17+ o exigir de facto** (pareamento
   moderno a falhar no libimobiledevice nas bancadas reais — a medir com
   aparelhos reais, não agora).
3. Quando entrar, entrar como **componente GPL separado, opcional, de download
   sob demanda** (secção 2), com nota no instalador e oferta de fonte — nunca
   importado em-processo nem compilado para dentro do executável principal.
4. Em Windows, o pré-requisito **já existe hoje**: o README do Diag exige o
   driver Apple USB (iTunes ou app Apple Devices) para qualquer diagnóstico
   de iPhone, com ou sem pmd3 — o libimobiledevice depende do mesmo driver.
   A primeira versão desta nota apresentava «bancadas limpas» como argumento
   a favor do libimobiledevice — errado: **os dois caminhos partilham exatamente
   o mesmo pré-requisito**. A recomendação de adiar assenta no tamanho do
   bundle e na cobertura atual já ser suficiente, não em drivers.

## 7. Pergunta que fica para o mentor

A pergunta original («a bancada tem software Apple?») caducou com a correção
da secção 4: o driver Apple é pré-requisito do diagnóstico iOS **hoje**, qualquer
que seja o caminho. A pergunta que sobra é a de produto: **quanto nos pesa
não suportar bem iOS 17+ até medirmos a falha em aparelhos reais?** Se houver
queixas de emparelhamento com iPhones recentes nas bancadas, o pmd3 sobe de
prioridade; se não houver, adiar é a decisão certa.
