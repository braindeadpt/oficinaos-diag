# Publicar o OficinaOS Diag na Microsoft Store

Guia completo para submeter a app como **gratuita** na Microsoft Store.
Distribuída pela Store, a app instala-se com um clique, atualiza sozinha e
**não dispara o SmartScreen** (a Microsoft assina o pacote).

> Estado: **publicada**. Listagem live em
> <https://apps.microsoft.com/detail/9P2BM91SFKFM> (editor: conta individual
> do Partner Center). Este guia fica como referência para **submeter versões
> novas** — cada release = novo `tools\build-store.ps1` + nova submissão
> (review ~24–72h), começando no passo 3.

---

## 1. Conta no Partner Center (uma vez)

1. Ir a <https://partner.microsoft.com/dashboard> → **Apps and games**.
2. Criar conta de programador:
   - **Individual** — gratuita; a Store mostra o teu nome pessoal como editor.
   - **Empresa** — paga (~$99 uma vez) e exige validação da empresa; mostra
     «Oficina de Sistemas» como editor. Recomendado se quisermos o nome da
     empresa na Store.
3. Em **Apps → New app**, reservar o nome **`OficinaOS Diag`**.

## 2. Copiar a identidade para o manifesto

No Partner Center: a tua app → **Product management → App identity**. Copiar:

| Campo | Para onde |
|---|---|
| `Package/Identity/Name` | `Name` em `packaging/store/AppxManifest.xml` |
| `Package/Identity/Publisher` | `Publisher` no mesmo ficheiro |

E substituir `REPLACE_PUBLISHER_DISPLAY_NAME` pelo nome legal (o que aparece
como editor na Store).

## 3. Gerar o pacote

```powershell
tools\build-store.ps1
```

Faz publish self-contained, gera o índice de recursos (tiles em scale-200) e
produz em `out-store\`:

- `OficinaOSDiag_<versão>_x64.msix` — pacote em si
- `OficinaOSDiag_<versão>_x64.msixupload` — **é este que se envia**

Notas:

- O `.msix` vai **não assinado** de propósito — a Store assina na publicação.
- A versão sai do `Version` no `.csproj`; a MSIX exige 4 segmentos
  (`0.1.9` → `0.1.9.0` automaticamente).
- Para testar localmente antes da Store, é preciso assinar com um certificado
  de teste — para iterar rápido continua a valer o ZIP normal.

## 4. Submissão — o que preencher

| Passo | O que meter |
|---|---|
| **Packages** | Upload do `.msixupload` |
| **Properties** | Categoria: **Utilitários / Utilities & tools** |
| **Age ratings** | Questionário IARC — sem conteúdo sensível → **Everyone / 3+** |
| **Pricing** | **Free** |
| **Availability** | Todos os mercados; publicar quando aprovada |
| **Store listings** | PT + EN mínimo (ES opcional) — textos em baixo |
| **Privacy policy** | URL pública obrigatória — usar `https://cloud.oficinaos.app/privacy.html` (já existe) ou a secção «Privacidade» do README |
| **Notes for certification** | Texto em baixo — inclui a justificação do `runFullTrust` |

### Porque `runFullTrust` (justificação para a review)

A Store deteta a capability restrita e pede uma justificação escrita. Texto
pronto a colar (inglês — a review é feita em inglês):

> This is a packaged classic (desktop bridge) app. It diagnoses phones over
> USB by spawning bundled child processes: Google's `adb.exe`
> (platform-tools) for Android and the libimobiledevice toolset for iOS.
> Child process spawning requires `runFullTrust`. The app is a free,
> open-source utility (MIT) — source: github.com/braindeadpt/oficinaos-diag.

### Notas de teste para a review

> The app works standalone without a phone: the main window, scan history,
> HTML/JSON export and settings all function without a device. USB scans
> require an Android phone with USB debugging or an iPhone with the Apple
> USB driver (iTunes / Apple Devices app). The on-device screen test serves
> a page on the local network (privateNetworkClientServer). Optional cloud
> features (send-to-shop, AI report) are opt-in buttons only.

## 5. Texto da listagem (PT + EN)

**Nome:** OficinaOS Diag
**Descrição curta (PT):** Diagnóstico de telemóveis por USB — bateria, ecrã, sensores. Grátis e local.
**Descrição curta (EN):** USB phone diagnostics — battery, screen, sensors. Free and local.

**Descrição longa (PT):**

> O OficinaOS Diag lê qualquer Android ou iPhone ligado por cabo: modelo,
> bateria (nível, ciclos, capacidade real), ecrã, sensores e armazenamento.
> O scan corre 100% no PC — nada sai sem autorização. Inclui teste de ecrã
> e de toque no próprio telemóvel, exportação em HTML/JSON e histórico de
> scans. Ferramenta gratuita do ecossistema OficinaOS, open source (MIT).
> iPhone: requer o driver USB da Apple (iTunes ou app «Apple Devices»).

**Descrição longa (EN):**

> OficinaOS Diag reads any Android or iPhone over USB: model, battery
> (level, cycles, real capacity), screen, sensors and storage. Scans run
> 100% locally — nothing leaves the PC without permission. Includes an
> on-device screen and touch test, HTML/JSON export and scan history. A
> free tool from the OficinaOS ecosystem, open source (MIT). iPhone
> requires the Apple USB driver (iTunes or Apple Devices app).

**Screenshots (obrigatório ≥1, 1366×768 ou superior):** janela principal,
resultado de um scan, página de teste no telemóvel. Tirar da app a correr.

## 6. Depois de publicado

- A app passa a instalar-se da Store com um clique — sem SmartScreen, e
  atualiza automaticamente para toda a gente.
- **Cada versão nova** = novo `build-store.ps1` + nova submissão (review
  ~24–72h). O ZIP nas GitHub Releases continua para quem preferir portátil.
- O histórico (`%APPDATA%\OficinaDiag`) migra para o espaço privado do
  pacote MSIX — quem muda do ZIP para a Store começa com histórico vazio.
