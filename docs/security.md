# Segurança — riscos conhecidos e plano

Revisão do ecossistema de 2026-10. Este ficheiro lista o que **ainda não está
resolvido** no diag e o caminho recomendado. Atualizar quando cada ponto fechar.

## 1. Atualizações: integridade ≠ autenticidade

**Hoje:** `Cloud/UpdateChecker.cs` descarrega o zip da última release do
GitHub e verifica o SHA-256 contra o `SHA256SUMS.txt` **da mesma release**.
Isto apanha downloads corrompidos ou incompletos (integridade), mas não prova
quem publicou (autenticidade): quem comprometer a conta GitHub ou o workflow
de release publica um zip e um checksum coerentes, e o updater executa-os
(PowerShell `-ExecutionPolicy Bypass`).

**Mitigação já feita (fix/review-2026-10):** o publicador está fixo — só são
aceites assets em
`https://github.com/braindeadpt/oficinaos-diag/releases/download/<tag>/…`
(https, host `github.com`, sem porta/userinfo/query, mesma tag da release).
Trava URLs manipulados ou desviados para outro repo; **não** trava um repo
comprometido.

**Correção recomendada (escolher uma):**

- **minisign / Ed25519 (barato, recomendado já):**
  1. `minisign -G` numa máquina offline; a chave privada **nunca** entra no
     GitHub (no máximo como secret de um ambiente protegido com aprovação
     manual — mas offline é melhor).
  2. No release: `minisign -Sm SHA256SUMS.txt` → publicar também
     `SHA256SUMS.txt.minisig`.
  3. Embutir a chave pública (base64, ~56 caracteres) como constante no exe
     e verificar a assinatura Ed25519 do `SHA256SUMS.txt` antes de confiar
     no hash (ex. NSec ou BouncyCastle; .NET 8 não traz Ed25519 nativo).
  4. Fail closed: sem `.minisig` válido, a atualização é recusada.
  5. Rotação: prever duas chaves públicas aceites durante a transição.
- **Authenticode / Azure Trusted Signing:** assinar o `OficinaDiag.exe` no
  workflow e, antes de aplicar, validar a assinatura (WinVerifyTrust) e o
  *subject* do certificado. Tem custo, mas resolve também o aviso do
  SmartScreen.

Depois de qualquer das duas: lançar uma versão nova para que os clientes
instalados passem a exigir a assinatura.

## 2. Shop token completo em PCs de técnicos

**Hoje:** para gerar relatórios IA, o diag guarda (DPAPI, `CurrentUser`) o
*shop token* completo — o mesmo da app, que permite publicar o portal,
mudar o `whatsapp-config`, ler reservas, etc. O DPAPI impede copiar o
ficheiro para outra máquina, mas não protege contra outro processo do mesmo
utilizador do Windows num PC de bancada partilhado.

**Follow-up recomendado (cloud + diag):** tokens com âmbito, ex.
`scope: ["ai-reports"]`, emitidos no dashboard da Cloud só para o diag,
um por PC, revogáveis individualmente, com rate limit próprio. O diag passa
a pedir esse token em vez do token da loja.
