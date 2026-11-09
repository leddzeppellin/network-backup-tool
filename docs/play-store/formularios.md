# Formulários do Play Console

Respostas sugeridas para os formulários de **Conteúdo do app** (Play Console → Política → Conteúdo
do app). Revise antes de enviar: as respostas são suas perante o Google.

---

## 1. Política de privacidade

URL:

```
https://github.com/leddzeppellin/network-backup-tool/blob/main/docs/privacidade.md
```

---

## 2. Permissão "Acesso a todos os arquivos" (MANAGE_EXTERNAL_STORAGE)

Aparece ao enviar o primeiro AAB, em **Conteúdo do app → Permissões de acesso a todos os arquivos**.

**Funcionalidade principal que exige a permissão** — escolha:

- **Migração de dados entre dispositivos** (*Device-to-device data migration*) — principal
- e, se o formulário permitir mais de uma, **Apps de backup e restauração**

**Descrição do uso** (em inglês, que é o que a equipe de revisão lê):

```
Network Backup Tool transfers user-selected files and folders between the user's own devices on the same local network (Android phones and Windows/Linux/macOS computers), for device backup and migration to a new device.

The core feature is copying whole folders chosen by the user, such as the camera folder (DCIM), Pictures, Documents, Download and WhatsApp media (Android/media/com.whatsapp), with their subfolders, to a computer or another phone, and receiving files from them. These folders contain files created by other apps, which cannot be read in bulk through MediaStore (non-media files such as documents and WhatsApp databases/attachments are not available there), and the Storage Access Framework does not allow selecting the storage root or the Download folder, nor reading large trees efficiently.

All files access is used only to read the folders the user explicitly selects to send, and to write received files to Download/NetBackupTool. Files are sent directly to the device the user chooses on the local network, protected by a 6-digit code; nothing is uploaded to any server. The app has no accounts, analytics or ads, and is open source (MIT): https://github.com/leddzeppellin/network-backup-tool
```

**Vídeo de demonstração**: o Google costuma pedir um link (YouTube não listado serve) mostrando o
uso da permissão. Grave 30–60 s da tela do celular mostrando:
1. Enviar → **Pastas do aparelho** (aparecem DCIM, WhatsApp etc.) → o pedido de "Acesso a todos os
   arquivos" → ativar → voltar ao app.
2. Escolher o PC na lista, digitar o código e enviar; mostrar os arquivos chegando no PC.

No Android: puxe a barra de notificações → **Gravar tela**.

---

## 3. Segurança dos dados (Data safety)

- O app coleta ou compartilha algum dos tipos de dados exigidos? → **Não**
  - Justificativa: os arquivos vão diretamente para um aparelho do próprio usuário na rede local, por
    ação dele, sem passar por servidores do desenvolvedor ou de terceiros; o app não tem servidores.
- Os dados são criptografados em trânsito? → **Não** (a transferência é feita na rede local; os
  blocos são conferidos por soma de verificação, mas não criptografados).
- Os usuários podem pedir a exclusão dos dados? → **Não se aplica** (o app não coleta dados).

---

## 4. Outros formulários

| Formulário | Resposta |
|---|---|
| Acesso ao app | Todas as funções estão disponíveis sem acesso especial (não há login). |
| Anúncios | Não contém anúncios. |
| Classificação de conteúdo | Categoria **Utilitário/Produtividade**; responda **Não** a todas as perguntas (sem violência, conteúdo sexual, linguagem, drogas, apostas, interação entre usuários ou compartilhamento de localização). Resultado esperado: Livre / PEGI 3. |
| Público-alvo | **18 anos ou mais** (é uma ferramenta técnica; evita as exigências de apps para crianças). |
| App de notícias | Não. |
| Apps de saúde / financeiros / governo | Não. |
| ID de publicidade | O app **não** usa ID de publicidade. |
