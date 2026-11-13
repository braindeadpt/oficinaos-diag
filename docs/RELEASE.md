# Release signing — fluxo offline

Regra de ouro: **a chave privada nunca toca no CI e nunca fica em
plaintext no disco**. Uma chave num GitHub secret só prova que o pipeline
correu — quem tiver push no repo podia alterar o workflow e assinar
qualquer zip. Assinar offline na máquina do dono é o que faz a assinatura
provar *origem*.

## Peças

| Peça | Onde vive |
|---|---|
| `release-key.dpapi` (PKCS#8 protegido com DPAPI `CurrentUser`, entropy `oficinaos-release`) | `%LOCALAPPDATA%\OficinaOS\` — só abre nesta conta de utilizador, nesta máquina. ACL owner-only |
| Backup `oficinaos-release-key.enc.pem` (PKCS#8 cifrado por passphrase — PBKDF2-SHA256 ×600k + AES-256) | `E:\` ou pen cifrada — para recuperação noutro PC |
| Chave pública | Embutida em `src/OficinaDiag/Cloud/UpdateChecker.cs` (`ReleaseSigningPublicKey`) |
| `SHA256SUMS.txt` | `version <x.y.z>` + `commit <sha>` + `<hash>  <ficheiro>` — gerado pelo `release.yml` |
| `SHA256SUMS.sig` | Assinatura base64 (P1363) sobre o SUMS — gerada offline por `tools/sign-release.ps1` |

## Setup (uma vez por máquina)

```powershell
.\tools\protect-release-key.ps1
```

Pede a passphrase do `.enc.pem` atual (sem eco), decifra só em memória e
grava os dois artefactos acima. A passphrase nova do backup é pedida
duas vezes — **nunca é impressa nem registada em log; guarda-a num gestor
de passwords**. No fim verifica que a pública derivada de ambos bate na
embutida no updater e apaga a cópia solta antiga.

## Como sai uma release

```powershell
git tag v0.4.0 && git push --tags
```

1. O `release.yml` corre testes, faz publish self-contained, empacota
   `oficinaos-diag-v<versão>-win-x64.zip`, **confere o conteúdo do zip**
   (ver abaixo), escreve `SHA256SUMS.txt` (`version` + `commit` + hash) e
   cria uma **release draft** no GitHub. Drafts são invisíveis para
   `releases/latest` — o updater ignora-as.
2. Na máquina do dono (pwsh 7+, `gh` autenticado):

   ```powershell
   .\tools\sign-release.ps1 -Tag v0.4.0                    # DPAPI, sem prompt
   .\tools\sign-release.ps1 -Tag v0.4.0 -KeyFile E:\oficinaos-release-key.enc.pem
   ```

   O script: descarrega o `SHA256SUMS.txt` do draft → confirma
   `version == tag` e `commit == commit do tag` → verifica que o commit é
   ancestor de `main` → pede `Assinar <tag> @ <sha>? [s/N]` → assina →
   faz upload do `.sig` → **publica o draft**.

   - `-Confirm:$false` salta a pergunta (automação).
   - `-NoPublish` testa tudo menos o publish — o draft fica assinado mas
     invisível.
   - Sem `.dpapi` e sem `-KeyFile`, o script falha a pedir
     `protect-release-key.ps1`.
3. O updater nos PCs vê a release publicada, valida a assinatura contra a
   chave pública embutida, exige `version == tag` e `> versão local`, e só
   então confia no hash e descarrega o zip.

## Verificação do conteúdo do pacote

O passo `Verify package contents` corre **depois** do empacotamento e
**antes** do checksum e do draft. Abre o zip e falha o job com uma de três
mensagens:

| Mensagem | Quando |
|---|---|
| `Pacote em falta: <zip> não existe` | o `Compress-Archive` não produziu o ficheiro |
| `Pacote ilegível: <zip> não abre (corrompido ou truncado)` | zip truncado, corrompido ou cortado a meio |
| `Pacote incompleto: falta <caminhos> no zip` | falta o exe, o `adb`, o `models.json` ou a página do teste (lista todos os que faltarem) |

Um pacote sem qualquer um deles é inútil mesmo que o updater o aceite, por
isso a falha é antes de haver algo para assinar. O `tools/fetch-tools.ps1`
corre antes do publish no CI; sem ele, localmente a verificação acusa — é o
comportamento correto, não um falso positivo.

A lista de caminhos vive num sítio só, `tools/package-files.txt` (uma por
linha, `#` comenta), lida pelo passo — não há cópia no YAML. Se esse
ficheiro faltar o passo falha com `Lista ausente: …`, para não verificar
zero ficheiros e passar. O `PackageFilesTests` compara a lista com os
`Content` com `CopyToPublishDirectory` dos dois csproj, nos dois sentidos:
acusa um caminho da lista que o publish não produz e um ficheiro publicado
que a lista não verifica, e garante que o workflow voltou a ler o ficheiro
em vez de ter uma lista própria.

## Recuperação (PC novo / máquina substituída)

1. Copia `oficinaos-release-key.enc.pem` do backup (`E:\`/pen) para o PC
   novo.
2. Corre:

   ```powershell
   .\tools\protect-release-key.ps1 -SourcePem C:\caminho\oficinaos-release-key.enc.pem
   ```

3. Introduz a passphrase do backup — cria o `.dpapi` local e um backup
   novo re-cifrado (podes trocar a passphrase aqui).

## Porque assim

- **Conta GitHub comprometida** → cria drafts e tags, mas sem a chave não
  produz um `.sig` válido → updater recusa.
- **Malware/backup exposto** → encontra `release-key.dpapi` (inútil fora
  desta conta/máquina) e `.enc.pem` (inútil sem a passphrase).
- **`version`+`commit` no SUMS assinado** → replay/downgrade e tag-moves
  falham a validação.
- **Passphrases**: pedidas com `Read-Host -AsSecureString`, nunca em
  output, chat, logs nem argumentos de linha de comando. Se uma
  passphrase aparecer num log, considera-a comprometida e roda o par.

## Rotação de chave

```powershell
.\tools\generate-release-key.ps1   # novo par; PEM fica na pasta do repo
```

1. Embutir a **nova pública** no `UpdateChecker` e lançar uma release —
   clients antigos continuam a validar com a chave que trazem, por isso a
   chave velha assina *essa* release de transição, e a partir daí só a
   nova.
2. Correr `protect-release-key.ps1` para proteger o **novo** par (grava
   `.dpapi` + backup novos) e aposentar os ficheiros antigos.

Nota: a primeira chave (gerada 2026-10) transitou brevemente por um
GitHub secret durante a implementação inicial e foi **rodada antes de
qualquer release a ter usado** — a pública embutida corresponde ao par
atual, cuja privada nunca saiu do armazenamento offline.
