# Release signing — fluxo offline

Regra de ouro: **a chave privada nunca toca no CI**. Uma chave num GitHub
secret só prova que o pipeline correu — quem tiver push no repo podia
alterar o workflow e assinar qualquer zip. Assinar offline na máquina do
dono é o que faz a assinatura provar *origem*.

## Peças

| Peça | Onde vive |
|---|---|
| Chave privada EC P-256 (`release-signing-key.pem`) | Cofre/pen cifrada do dono — fora deste repo e fora do CI |
| Chave pública | Embutida em `src/OficinaDiag/Cloud/UpdateChecker.cs` (`ReleaseSigningPublicKey`) |
| `SHA256SUMS.txt` | `version <x.y.z>` + `<hash>  <ficheiro>` — gerado pelo `release.yml` |
| `SHA256SUMS.sig` | Assinatura base64 sobre o SUMS — gerada offline por `tools/sign-release.ps1` |

## Como sai uma release

```powershell
git tag v0.3.0 && git push --tags
```

1. O `release.yml` corre testes, faz publish self-contained, empacota
   `oficinaos-diag-v<versão>-win-x64.zip`, escreve `SHA256SUMS.txt`
   (`version` + hash) e cria uma **release draft** no GitHub.
   Drafts são invisíveis para `releases/latest` — o updater ignora-as.
2. Na máquina do dono (pwsh 7+ e `gh` autenticado), com a PEM no cofre:

   ```powershell
   .\tools\sign-release.ps1 -Tag v0.3.0 -KeyFile E:\cofre\release-key.pem
   ```

   O script: descarrega o `SHA256SUMS.txt` do draft → confirma que a linha
   `version` bate no tag → assina com a PEM → faz upload do `.sig` →
   **publica o draft**.
3. O updater nos PCs vê a release publicada, valida a assinatura contra a
   chave pública embutida, exige `version == tag` e `> versão local`, e só
   então confia no hash e descarrega o zip.

## Porque assim

- **Conta GitHub comprometida** → consegue criar drafts e tags, mas sem a
  PEM não produz um `.sig` válido → updater recusa.
- **`version` no SUMS assinado** → um SUMS velho válido republicado num
  tag mais alto falha `version == tag` (anti-downgrade/replay).
- **Sem `.sig`** → updater recusa; nunca cai para "só checksum".

## Rotação de chave

```powershell
.\tools\generate-release-key.ps1   # novo par; PEM fica na pasta do repo
```

1. Embutir a **nova pública** no `UpdateChecker` e lançar uma release —
   clients antigos continuam a validar com a chave que trazem, por isso a
   chave velha assina *essa* release de transição, e a partir daí só a nova.
2. Aposentar a PEM antiga (guardar cópia para assinar a transição).

Nota: a primeira chave (gerada 2026-10) transitou brevemente por um GitHub
secret durante a implementação inicial e foi **rodada antes de qualquer
release a ter usado** — a pública embutida corresponde ao par atual, cuja
privada nunca saiu do armazenamento offline.
