using System.Globalization;

namespace Nbt.Core;

/// <summary>
/// Textos do programa em português, inglês e espanhol. <see cref="T"/> devolve o texto no idioma
/// escolhido; os marcadores {0}, {1}... seguem string.Format.
/// </summary>
public static class L
{
    public static readonly string[] Languages = ["pt", "en", "es"];

    /// <summary>Idioma em uso: "pt", "en" ou "es".</summary>
    public static string Lang { get; private set; } = Detect();

    /// <summary>"auto" (ou vazio) segue o idioma do Windows.</summary>
    public static void Set(string? code) =>
        Lang = string.IsNullOrEmpty(code) || code == "auto" || !Languages.Contains(code) ? Detect() : code;

    public static string Detect()
    {
        var name = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        // Modo invariante (Linux sem ICU): o idioma vem das variáveis do sistema, ex.: LANG=pt_BR.UTF-8.
        if (name == "iv" || name.Length == 0)
        {
            var env = new[] { "LC_ALL", "LC_MESSAGES", "LANG" }
                .Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
            name = env.Length >= 2 ? env[..2].ToLowerInvariant() : "en";
        }
        return name switch
        {
            "pt" => "pt",
            "es" => "es",
            _ => "en",
        };
    }

    public static string T(string key, params object?[] args)
    {
        if (!Table.TryGetValue(key, out var t)) return key;
        var s = Lang switch { "en" => t.En, "es" => t.Es, _ => t.Pt };
        return args.Length == 0 ? s : string.Format(CultureInfo.CurrentCulture, s, args);
    }

    /// <summary>Textos faltando ou com marcadores {n} diferentes entre os idiomas (usado no teste).</summary>
    public static IEnumerable<string> Problems()
    {
        static string Marks(string s) => string.Join(",", System.Text.RegularExpressions.Regex
            .Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());
        foreach (var (key, t) in Table)
        {
            if (t.Pt.Length == 0 || t.En.Length == 0 || t.Es.Length == 0) yield return key + ": texto vazio";
            else if (Marks(t.Pt) != Marks(t.En) || Marks(t.Pt) != Marks(t.Es)) yield return key + ": marcadores diferentes";
        }
    }

    private static readonly Dictionary<string, (string Pt, string En, string Es)> Table = new()
    {
        // ---------------------------------------------------------------- envio (núcleo)
        ["send.badCode"] = ("Código incorreto. Confira o código mostrado no computador que recebe.",
                            "Wrong code. Check the code shown on the receiving computer.",
                            "Código incorrecto. Revisa el código que aparece en el equipo que recibe."),
        ["send.refused"] = ("O receptor recusou a conexão.", "The receiver refused the connection.", "El receptor rechazó la conexión."),
        ["send.refusedReason"] = ("Receptor recusou: {0}", "Receiver refused: {0}", "El receptor rechazó: {0}"),
        ["send.connected"] = ("Conectado a {0}. Enviando lista de {1:N0} itens...", "Connected to {0}. Sending list of {1:N0} items...", "Conectado a {0}. Enviando lista de {1:N0} elementos..."),
        ["send.skipping"] = ("{0:N0} arquivos já estão no destino e serão pulados ({1}).", "{0:N0} files are already at the destination and will be skipped ({1}).", "{0:N0} archivos ya están en el destino y se omitirán ({1})."),
        ["send.transferring"] = ("Transferindo {0:N0} arquivos ({1}) por {2} conexões.", "Transferring {0:N0} files ({1}) over {2} connections.", "Transfiriendo {0:N0} archivos ({1}) por {2} conexiones."),
        ["send.dataRefused"] = ("Conexão de dados recusada pelo receptor.", "Data connection refused by the receiver.", "El receptor rechazó la conexión de datos."),
        ["send.fromShadow"] = ("Em uso, copiado da Shadow Copy: {0}", "In use, copied from Shadow Copy: {0}", "En uso, copiado desde Shadow Copy: {0}"),
        ["send.lockedHint"] = ("Feche o programa que usa o arquivo, ou abra o Network Backup Tool como administrador para usar Shadow Copy.",
                               "Close the program using the file, or run Network Backup Tool as administrator to use Shadow Copy.",
                               "Cierra el programa que usa el archivo, o abre Network Backup Tool como administrador para usar Shadow Copy."),
        ["send.readFailed"] = ("falha de leitura", "read failure", "error de lectura"),
        ["send.fileShrank"] = ("o arquivo diminuiu durante a cópia", "the file shrank during the copy", "el archivo se redujo durante la copia"),
        ["send.readError"] = ("ERRO ao ler {0}: {1}", "ERROR reading {0}: {1}", "ERROR al leer {0}: {1}"),
        ["send.noEndAck"] = ("Receptor não confirmou o fim dos dados.", "The receiver did not confirm the end of the data.", "El receptor no confirmó el fin de los datos."),

        // ---------------------------------------------------------------- recepção (núcleo)
        ["recv.noDiscovery"] = ("Descoberta automática indisponível ({0}). Use o IP manualmente.", "Automatic discovery unavailable ({0}). Use the IP address manually.", "Detección automática no disponible ({0}). Usa la IP manualmente."),
        ["recv.connError"] = ("Erro na conexão de {0}: {1}", "Connection error from {0}: {1}", "Error en la conexión de {0}: {1}"),
        ["recv.locked"] = ("Tentativa de {0} ({1}) recusada: bloqueio por códigos errados em vigor.", "Attempt from {0} ({1}) refused: lockout for wrong codes in effect.", "Intento de {0} ({1}) rechazado: bloqueo por códigos incorrectos en vigor."),
        ["recv.badCode"] = ("Tentativa com código errado de {0} ({1}).", "Wrong code attempt from {0} ({1}).", "Intento con código incorrecto de {0} ({1})."),
        ["recv.lockout"] = ("Muitas tentativas erradas: novas conexões bloqueadas por {0:0} minutos.", "Too many wrong attempts: new connections blocked for {0:0} minutes.", "Demasiados intentos incorrectos: nuevas conexiones bloqueadas por {0:0} minutos."),
        ["recv.codeChanged"] = ("Muitas tentativas erradas: o código foi trocado.", "Too many wrong attempts: the code was changed.", "Demasiados intentos incorrectos: se cambió el código."),
        ["recv.rejected"] = ("Recusado {0}: {1}", "Refused {0}: {1}", "Rechazado {0}: {1}"),
        ["recv.receiving"] = ("Recebendo de {0} ({1}) em {2}", "Receiving from {0} ({1}) into {2}", "Recibiendo de {0} ({1}) en {2}"),
        ["recv.dropped"] = ("Conexão com o remetente caiu antes do fim.", "The connection to the sender dropped before the end.", "La conexión con el remitente se cayó antes del final."),
        ["recv.sessionEnd"] = ("Fim da sessão de {0}: {1:N0} ok, {2:N0} com falha.", "Session from {0} finished: {1:N0} ok, {2:N0} failed.", "Fin de la sesión de {0}: {1:N0} correctos, {2:N0} con error."),
        ["recv.noSpace"] = ("Espaço insuficiente no destino: precisa de {0}, livre {1}.", "Not enough space at the destination: needs {0}, free {1}.", "Espacio insuficiente en el destino: necesita {0}, libre {1}."),
        ["recv.badIndex"] = ("Índice de arquivo inválido.", "Invalid file index.", "Índice de archivo no válido."),
        ["recv.sourceRead"] = ("{0}: não foi possível ler na origem ({1})", "{0}: could not be read at the source ({1})", "{0}: no se pudo leer en el origen ({1})"),
        ["recv.badBlock"] = ("Bloco inválido.", "Invalid block.", "Bloque no válido."),
        ["recv.integrity"] = ("{0}: falha de integridade (dados corrompidos na rede)", "{0}: integrity failure (data corrupted on the network)", "{0}: error de integridad (datos dañados en la red)"),
        ["recv.writeError"] = ("{0}: erro ao gravar ({1})", "{0}: write error ({1})", "{0}: error al escribir ({1})"),
        ["recv.incomplete"] = ("{0}: incompleto", "{0}: incomplete", "{0}: incompleto"),
        ["recv.reportFail"] = ("Não foi possível gravar o relatório: {0}", "Could not write the report: {0}", "No se pudo guardar el informe: {0}"),

        ["report.title"] = ("Network Backup Tool - relatório de cópia", "Network Backup Tool - copy report", "Network Backup Tool - informe de copia"),
        ["report.origin"] = ("Origem:   {0} ({1})", "Source:   {0} ({1})", "Origen:   {0} ({1})"),
        ["report.start"] = ("Início:   {0}", "Started:  {0}", "Inicio:   {0}"),
        ["report.end"] = ("Fim:      {0}", "Finished: {0}", "Fin:      {0}"),
        ["report.copied"] = ("Copiados: {0:N0} arquivos ({1})", "Copied:   {0:N0} files ({1})", "Copiados: {0:N0} archivos ({1})"),
        ["report.skipped"] = ("Pulados (já existiam iguais): {0:N0}", "Skipped (already identical): {0:N0}", "Omitidos (ya existían iguales): {0:N0}"),
        ["report.failed"] = ("Falhas:   {0:N0}", "Failed:   {0:N0}", "Errores:  {0:N0}"),
        ["report.errors"] = ("Erros:", "Errors:", "Errores:"),

        // ---------------------------------------------------------------- varredura e VSS (núcleo)
        ["scan.noAccess"] = ("Sem acesso a {0}: {1}", "No access to {0}: {1}", "Sin acceso a {0}: {1}"),
        ["scan.notFound"] = ("Não encontrado: {0}", "Not found: {0}", "No encontrado: {0}"),
        ["man.invalid"] = ("Manifesto inválido.", "Invalid manifest.", "Manifiesto no válido."),
        ["man.badSize"] = ("Tamanho inválido no manifesto.", "Invalid size in the manifest.", "Tamaño no válido en el manifiesto."),
        ["man.badPath"] = ("Caminho inválido recebido: {0}", "Invalid path received: {0}", "Ruta no válida recibida: {0}"),
        ["man.outside"] = ("Caminho fora da pasta de destino: {0}", "Path outside the destination folder: {0}", "Ruta fuera de la carpeta de destino: {0}"),

        ["vss.needAdmin"] = ("Arquivo em uso: para copiá-lo via Shadow Copy, abra o programa como administrador.", "File in use: to copy it through Shadow Copy, run the program as administrator.", "Archivo en uso: para copiarlo con Shadow Copy, abre el programa como administrador."),
        ["vss.creating"] = ("Arquivo em uso: criando Shadow Copy de {0} (alguns segundos)...", "File in use: creating a Shadow Copy of {0} (a few seconds)...", "Archivo en uso: creando Shadow Copy de {0} (unos segundos)..."),
        ["vss.createFailed"] = ("Não foi possível criar a Shadow Copy de {0} (código {1}).", "Could not create the Shadow Copy of {0} (code {1}).", "No se pudo crear la Shadow Copy de {0} (código {1})."),
        ["vss.ready"] = ("Shadow Copy pronta para {0}: arquivos em uso serão lidos dela.", "Shadow Copy ready for {0}: files in use will be read from it.", "Shadow Copy lista para {0}: los archivos en uso se leerán desde ella."),
        ["vss.unavailable"] = ("Shadow Copy indisponível para {0}: {1}", "Shadow Copy unavailable for {0}: {1}", "Shadow Copy no disponible para {0}: {1}"),
        ["vss.deleteFailed"] = ("Não consegui apagar a Shadow Copy {0}: {1}", "Could not delete Shadow Copy {0}: {1}", "No se pudo borrar la Shadow Copy {0}: {1}"),

        // ---------------------------------------------------------------- interface: geral
        ["app.admin"] = ("Network Backup Tool (Administrador)", "Network Backup Tool (Administrator)", "Network Backup Tool (Administrador)"),
        ["tray.open"] = ("Abrir", "Open", "Abrir"),
        ["tray.exit"] = ("Sair", "Exit", "Salir"),
        ["tray.receiving"] = ("Network Backup Tool — recebendo, código {0}", "Network Backup Tool — receiving, code {0}", "Network Backup Tool — recibiendo, código {0}"),
        ["tray.stopped"] = ("Network Backup Tool — parado", "Network Backup Tool — stopped", "Network Backup Tool — detenido"),
        ["tray.keepsTitle"] = ("Continua recebendo", "Still receiving", "Sigue recibiendo"),
        ["tray.keepsText"] = ("O Network Backup Tool segue aguardando cópias. Clique no ícone para abrir ou use \"Sair\" no menu.",
                              "Network Backup Tool is still waiting for copies. Click the icon to open it, or use \"Exit\" in its menu.",
                              "Network Backup Tool sigue esperando copias. Haz clic en el icono para abrirlo o usa \"Salir\" en el menú."),
        ["busy.wait"] = ("Espere a cópia em andamento terminar.", "Wait for the copy in progress to finish.", "Espera a que termine la copia en curso."),
        ["busy.stop"] = ("Há cópia em andamento. Parar mesmo assim?", "A copy is in progress. Stop anyway?", "Hay una copia en curso. ¿Detener de todos modos?"),
        ["busy.close"] = ("Há cópia em andamento. Fechar mesmo assim?", "A copy is in progress. Close anyway?", "Hay una copia en curso. ¿Cerrar de todos modos?"),

        // ---------------------------------------------------------------- interface: enviar
        ["tab.send"] = ("Enviar  (PC do cliente)", "Send  (client PC)", "Enviar  (PC del cliente)"),
        ["send.what"] = ("1. O que copiar (arraste pastas e arquivos aqui)", "1. What to copy (drag folders and files here)", "1. Qué copiar (arrastra carpetas y archivos aquí)"),
        ["send.userFolders"] = ("Pastas do usuário", "User folders", "Carpetas del usuario"),
        ["send.addFolder"] = ("Adicionar pasta...", "Add folder...", "Agregar carpeta..."),
        ["send.addFiles"] = ("Adicionar arquivos...", "Add files...", "Agregar archivos..."),
        ["send.remove"] = ("Remover", "Remove", "Quitar"),
        ["send.ignore"] = ("Ignorar:", "Ignore:", "Ignorar:"),
        ["send.where"] = ("2. Para onde", "2. Where to", "2. A dónde"),
        ["send.find"] = ("Procurar na rede", "Search network", "Buscar en la red"),
        ["send.computer"] = ("Computador (nome ou IP):", "Computer (name or IP):", "Equipo (nombre o IP):"),
        ["send.connections"] = ("    Conexões paralelas:", "    Parallel connections:", "    Conexiones paralelas:"),
        ["send.code"] = ("Código do receptor:", "Receiver code:", "Código del receptor:"),
        ["send.adminOn"] = ("✔ Administrador: arquivos em uso (Outlook, bancos de dados) serão lidos via Shadow Copy.",
                            "✔ Administrator: files in use (Outlook, databases) will be read through Shadow Copy.",
                            "✔ Administrador: los archivos en uso (Outlook, bases de datos) se leerán con Shadow Copy."),
        ["send.adminOff"] = ("Arquivos em uso (Outlook aberto, bancos de dados) só são copiados como administrador.",
                             "Files in use (open Outlook, databases) are only copied when running as administrator.",
                             "Los archivos en uso (Outlook abierto, bases de datos) solo se copian como administrador."),
        ["send.reopenAdmin"] = ("Reabrir como administrador", "Reopen as administrator", "Reabrir como administrador"),
        ["send.send"] = ("Enviar", "Send", "Enviar"),
        ["send.cancel"] = ("Cancelar", "Cancel", "Cancelar"),
        ["send.pickFolders"] = ("Pastas para copiar", "Folders to copy", "Carpetas para copiar"),
        ["send.pickFiles"] = ("Arquivos para copiar", "Files to copy", "Archivos para copiar"),
        ["send.searching"] = ("Procurando receptores na rede...", "Searching the network for receivers...", "Buscando receptores en la red..."),
        ["send.noneFound"] = ("Nenhum receptor respondeu. Confira se a recepção está iniciada no outro PC, ou digite o IP dele.",
                              "No receiver answered. Check that receiving is started on the other PC, or type its IP address.",
                              "Ningún receptor respondió. Comprueba que la recepción esté iniciada en el otro PC, o escribe su IP."),
        ["send.found"] = ("Encontrado(s): {0}", "Found: {0}", "Encontrado(s): {0}"),
        ["send.needSource"] = ("Adicione ao menos uma pasta ou arquivo.", "Add at least one folder or file.", "Agrega al menos una carpeta o archivo."),
        ["send.needTarget"] = ("Informe o computador que vai receber (use \"Procurar na rede\" ou digite o IP).",
                               "Enter the receiving computer (use \"Search network\" or type its IP).",
                               "Indica el equipo que va a recibir (usa \"Buscar en la red\" o escribe la IP)."),
        ["send.needCode"] = ("Digite o código de 6 dígitos que aparece no computador que recebe.",
                             "Type the 6-digit code shown on the receiving computer.",
                             "Escribe el código de 6 dígitos que aparece en el equipo que recibe."),
        ["send.listing"] = ("Listando arquivos...", "Listing files...", "Listando archivos..."),
        ["send.listingN"] = ("Listando... {0:N0} arquivos", "Listing... {0:N0} files", "Listando... {0:N0} archivos"),
        ["send.warning"] = ("Aviso: {0}", "Warning: {0}", "Aviso: {0}"),
        ["send.cloudOnly"] = ("{0:N0} arquivos estão só na nuvem (OneDrive) e não foram baixados.", "{0:N0} files are cloud-only (OneDrive) and were not downloaded.", "{0:N0} archivos están solo en la nube (OneDrive) y no se descargaron."),
        ["send.scanned"] = ("{0:N0} arquivos, {1}.", "{0:N0} files, {1}.", "{0:N0} archivos, {1}."),
        ["send.done"] = ("Concluído em {0}: {1:N0} copiados, {2:N0} já existiam, {3:N0} com falha. Média {4}.",
                         "Finished in {0}: {1:N0} copied, {2:N0} already there, {3:N0} failed. Average {4}.",
                         "Terminado en {0}: {1:N0} copiados, {2:N0} ya existían, {3:N0} con error. Promedio {4}."),
        ["send.failLine"] = ("  FALHA: {0}", "  FAILED: {0}", "  ERROR: {0}"),
        ["send.someFailed"] = ("{0:N0} arquivo(s) não foram copiados. Veja a lista no registro (e no relatório gravado no destino).",
                               "{0:N0} file(s) were not copied. See the list in the log (and in the report saved at the destination).",
                               "{0:N0} archivo(s) no se copiaron. Mira la lista en el registro (y en el informe guardado en el destino)."),
        ["send.canceled"] = ("Cancelado. O que já foi copiado fica no destino; ao enviar de novo, continua de onde parou.",
                             "Canceled. What was already copied stays at the destination; sending again resumes where it stopped.",
                             "Cancelado. Lo ya copiado queda en el destino; al enviar de nuevo, continúa donde se quedó."),
        ["send.canceledEx"] = ("Cancelado ({0}).", "Canceled ({0}).", "Cancelado ({0})."),
        ["send.error"] = ("ERRO: {0}", "ERROR: {0}", "ERROR: {0}"),
        ["send.connectFailed"] = ("Não consegui conectar em {0}:{1}.\n\n{2}\n\nConfira se a recepção está iniciada no outro PC e se o firewall dele permite o Network Backup Tool.",
                                  "Could not connect to {0}:{1}.\n\n{2}\n\nCheck that receiving is started on the other PC and that its firewall allows Network Backup Tool.",
                                  "No se pudo conectar a {0}:{1}.\n\n{2}\n\nComprueba que la recepción esté iniciada en el otro PC y que su firewall permita Network Backup Tool."),

        // ---------------------------------------------------------------- interface: receber
        ["tab.receive"] = ("Receber  (meu PC)", "Receive  (my PC)", "Recibir  (mi PC)"),
        ["recv.saveIn"] = ("Salvar em (uma subpasta por computador de origem)", "Save to (one subfolder per source computer)", "Guardar en (una subcarpeta por equipo de origen)"),
        ["recv.choose"] = ("Escolher...", "Choose...", "Elegir..."),
        ["recv.openFolder"] = ("Abrir pasta", "Open folder", "Abrir carpeta"),
        ["recv.pickDest"] = ("Pasta onde os backups serão gravados", "Folder where backups will be saved", "Carpeta donde se guardarán las copias"),
        ["recv.tellClient"] = ("No PC do cliente, informe", "On the client PC, enter", "En el PC del cliente, indica"),
        ["recv.newCode"] = ("Gerar novo código", "New code", "Generar código nuevo"),
        ["recv.firewall"] = ("Liberar no firewall", "Allow in firewall", "Permitir en el firewall"),
        ["recv.persist"] = ("Modo persistente (deixa este PC sempre pronto para receber)", "Persistent mode (keeps this PC always ready to receive)", "Modo persistente (deja este PC siempre listo para recibir)"),
        ["recv.chkTray"] = ("Ao fechar a janela, continuar recebendo (ícone perto do relógio)", "When closing the window, keep receiving (icon next to the clock)", "Al cerrar la ventana, seguir recibiendo (icono junto al reloj)"),
        ["recv.chkAutostart"] = ("Iniciar com o Windows, já recebendo", "Start with Windows, already receiving", "Iniciar con Windows, ya recibiendo"),
        ["recv.chkFixed"] = ("Código fixo (não muda a cada uso)", "Fixed code (does not change between uses)", "Código fijo (no cambia en cada uso)"),
        ["recv.start"] = ("Iniciar recepção", "Start receiving", "Iniciar recepción"),
        ["recv.stop"] = ("Parar recepção", "Stop receiving", "Detener recepción"),
        ["recv.addresses"] = ("Computador: {0}\nEndereços:\n{1}", "Computer: {0}\nAddresses:\n{1}", "Equipo: {0}\nDirecciones:\n{1}"),
        ["recv.stopped"] = ("Recepção parada.", "Receiving stopped.", "Recepción detenida."),
        ["recv.destError"] = ("Não consegui usar a pasta de destino:\n{0}", "Could not use the destination folder:\n{0}", "No se pudo usar la carpeta de destino:\n{0}"),
        ["recv.notifyStartTitle"] = ("Recebendo arquivos", "Receiving files", "Recibiendo archivos"),
        ["recv.notifyStart"] = ("{0} começou a enviar.", "{0} started sending.", "{0} empezó a enviar."),
        ["recv.doneStatus"] = ("{0}: concluído — {1:N0} copiados ({2}), {3:N0} já existiam, {4:N0} falha(s).",
                               "{0}: finished — {1:N0} copied ({2}), {3:N0} already there, {4:N0} failed.",
                               "{0}: terminado — {1:N0} copiados ({2}), {3:N0} ya existían, {4:N0} con error."),
        ["recv.notifyDoneTitle"] = ("Cópia concluída", "Copy finished", "Copia terminada"),
        ["recv.notifyDone"] = ("{0}: {1:N0} copiados.", "{0}: {1:N0} copied.", "{0}: {1:N0} copiados."),
        ["recv.notifyDoneFail"] = ("{0}: {1:N0} copiados, {2:N0} com falha.", "{0}: {1:N0} copied, {2:N0} failed.", "{0}: {1:N0} copiados, {2:N0} con error."),
        ["recv.portError"] = ("Não consegui abrir a porta {0}: {1}\n\nOutro Network Backup Tool já está recebendo neste PC?",
                              "Could not open port {0}: {1}\n\nIs another Network Backup Tool already receiving on this PC?",
                              "No se pudo abrir el puerto {0}: {1}\n\n¿Hay otro Network Backup Tool recibiendo en este PC?"),
        ["recv.waiting"] = ("Aguardando na porta {0}. No PC do cliente: Enviar → escolha este computador → digite o código.",
                            "Waiting on port {0}. On the client PC: Send → pick this computer → type the code.",
                            "Esperando en el puerto {0}. En el PC del cliente: Enviar → elige este equipo → escribe el código."),
        ["recv.fwCreated"] = ("Regra de firewall criada.", "Firewall rule created.", "Regla de firewall creada."),
        ["recv.fwNotConfirmed"] = ("O netsh não confirmou a regra de firewall.", "netsh did not confirm the firewall rule.", "netsh no confirmó la regla de firewall."),
        ["recv.fwCanceled"] = ("Liberação do firewall cancelada.", "Firewall change canceled.", "Cambio en el firewall cancelado."),
        ["recv.waitingConn"] = ("Aguardando conexão...", "Waiting for a connection...", "Esperando conexión..."),
        ["recv.many"] = ("{0} computadores enviando — {1} de {2} — {3}", "{0} computers sending — {1} of {2} — {3}", "{0} equipos enviando — {1} de {2} — {3}"),

        ["progress"] = ("{0:N0} de {1:N0} arquivos — {2} de {3} — {4}", "{0:N0} of {1:N0} files — {2} of {3} — {4}", "{0:N0} de {1:N0} archivos — {2} de {3} — {4}"),
        ["progress.eta"] = (" — faltam {0}", " — {0} left", " — faltan {0}"),
        ["progress.failed"] = (" — {0:N0} falha(s)", " — {0:N0} failed", " — {0:N0} con error"),

        // ---------------------------------------------------------------- menus
        ["menu.language"] = ("Idioma / Language", "Language / Idioma", "Idioma / Language"),
        ["menu.langAuto"] = ("Automático (idioma do Windows)", "Automatic (Windows language)", "Automático (idioma de Windows)"),
        ["menu.langChanged"] = ("O idioma muda ao reabrir o programa. Reabrir agora?", "The language changes when the program reopens. Reopen now?", "El idioma cambia al reabrir el programa. ¿Reabrir ahora?"),
        ["menu.help"] = ("Ajuda", "Help", "Ayuda"),
        ["menu.howTo"] = ("Como usar", "How to use", "Cómo usar"),
        ["menu.update"] = ("Verificar atualizações...", "Check for updates...", "Buscar actualizaciones..."),
        ["menu.reports"] = ("Abrir pasta de relatórios", "Open reports folder", "Abrir carpeta de informes"),
        ["menu.project"] = ("Página do projeto no GitHub", "Project page on GitHub", "Página del proyecto en GitHub"),
        ["menu.about"] = ("Sobre", "About", "Acerca de"),

        ["howto.title"] = ("Como usar", "How to use", "Cómo usar"),
        ["howto.text"] = (
            "No seu PC (quem recebe):\n" +
            "  1. Receber → Iniciar recepção.\n" +
            "  2. Na primeira vez, permita o acesso no aviso do firewall.\n" +
            "  3. Anote o código de 6 dígitos.\n\n" +
            "No PC do cliente (quem envia):\n" +
            "  1. Abra o mesmo programa, em Enviar.\n" +
            "  2. Adicione o que copiar (Pastas do usuário, pastas ou arquivos, ou arraste para a janela).\n" +
            "  3. Clique no seu computador, que aparece sozinho na lista (ou digite o IP), digite o código e clique em Enviar.\n\n" +
            "Os arquivos chegam na pasta de destino, numa subpasta com o nome do PC do cliente, junto com um relatório.\n" +
            "Se a cópia for interrompida, envie de novo: o que já chegou igual é pulado.\n" +
            "No Windows, arquivos em uso (Outlook aberto, etc.) só são copiados com o programa aberto como administrador.",
            "On your PC (receiving):\n" +
            "  1. Receive → Start receiving.\n" +
            "  2. The first time, allow access in the firewall prompt.\n" +
            "  3. Note the 6-digit code.\n\n" +
            "On the client PC (sending):\n" +
            "  1. Open the same program, in Send.\n" +
            "  2. Add what to copy (User folders, folders or files, or drag them onto the window).\n" +
            "  3. Click your computer, which shows up in the list by itself (or type its IP), type the code and click Send.\n\n" +
            "Files arrive in the destination folder, in a subfolder named after the client PC, together with a report.\n" +
            "If the copy is interrupted, just send again: files already identical are skipped.\n" +
            "On Windows, files in use (open Outlook, etc.) are only copied when the program runs as administrator.",
            "En tu PC (el que recibe):\n" +
            "  1. Recibir → Iniciar recepción.\n" +
            "  2. La primera vez, permite el acceso en el aviso del firewall.\n" +
            "  3. Anota el código de 6 dígitos.\n\n" +
            "En el PC del cliente (el que envía):\n" +
            "  1. Abre el mismo programa, en Enviar.\n" +
            "  2. Agrega qué copiar (Carpetas del usuario, carpetas o archivos, o arrástralos a la ventana).\n" +
            "  3. Haz clic en tu equipo, que aparece solo en la lista (o escribe la IP), escribe el código y haz clic en Enviar.\n\n" +
            "Los archivos llegan a la carpeta de destino, en una subcarpeta con el nombre del PC del cliente, junto con un informe.\n" +
            "Si la copia se interrumpe, envía de nuevo: lo que ya llegó igual se omite.\n" +
            "En Windows, los archivos en uso (Outlook abierto, etc.) solo se copian con el programa abierto como administrador."),

        ["about.text"] = ("Versão {0}\n\nCopia arquivos entre computadores da rede local, sem compartilhamento do Windows.\n\n© 2026 Ricardo Valério da Silva\nLicença MIT — código aberto",
                          "Version {0}\n\nCopies files between computers on the local network, without Windows file sharing.\n\n© 2026 Ricardo Valério da Silva\nMIT License — open source",
                          "Versión {0}\n\nCopia archivos entre equipos de la red local, sin el uso compartido de Windows.\n\n© 2026 Ricardo Valério da Silva\nLicencia MIT — código abierto"),
        ["about.ok"] = ("Fechar", "Close", "Cerrar"),

        // ---------------------------------------------------------------- interface 2.0
        ["nav.send"] = ("Enviar", "Send", "Enviar"),
        ["nav.receive"] = ("Receber", "Receive", "Recibir"),
        ["nav.settings"] = ("Configurações", "Settings", "Configuración"),
        ["nav.about"] = ("Sobre", "About", "Acerca de"),
        ["send.subtitle"] = ("Use no PC do cliente para mandar os arquivos.", "Use on the client PC to send the files.", "Úsalo en el PC del cliente para enviar los archivos."),
        ["recv.subtitle"] = ("Use no seu PC para receber os arquivos.", "Use on your PC to receive the files.", "Úsalo en tu PC para recibir los archivos."),
        ["send.whatTitle"] = ("O que copiar", "What to copy", "Qué copiar"),
        ["send.dropHint"] = ("Arraste pastas e arquivos para cá", "Drag folders and files here", "Arrastra carpetas y archivos aquí"),
        ["send.whereTitle"] = ("Para onde", "Where to", "A dónde"),
        ["send.foundLabel"] = ("Encontrados na rede", "Found on the network", "Encontrados en la red"),
        ["send.lookingShort"] = ("Procurando computadores recebendo...", "Looking for receiving computers...", "Buscando equipos que estén recibiendo..."),
        ["send.noneYet"] = ("Nenhum encontrado ainda. Inicie a recepção no outro PC ou digite o IP.", "None found yet. Start receiving on the other PC or type its IP.", "Ninguno encontrado aún. Inicia la recepción en el otro PC o escribe la IP."),
        ["send.typeHost"] = ("Ou digite o nome ou IP", "Or type the name or IP", "O escribe el nombre o la IP"),
        ["send.codeTitle"] = ("Código do receptor", "Receiver code", "Código del receptor"),
        ["send.details"] = ("Detalhes", "Details", "Detalles"),
        ["send.ready"] = ("Pronto para enviar.", "Ready to send.", "Listo para enviar."),
        ["recv.codeFor"] = ("Código para o cliente", "Code for the client", "Código para el cliente"),
        ["recv.on"] = ("Recebendo", "Receiving", "Recibiendo"),
        ["recv.off"] = ("Parado", "Stopped", "Detenido"),
        ["recv.sessionsTitle"] = ("Cópias em andamento", "Copies in progress", "Copias en curso"),
        ["recv.noSessions"] = ("Nenhuma cópia no momento.", "No copies right now.", "Ninguna copia en este momento."),
        ["recv.chkAutostartAny"] = ("Iniciar com o sistema, já recebendo", "Start with the system, already receiving", "Iniciar con el sistema, ya recibiendo"),
        ["set.appearance"] = ("Aparência", "Appearance", "Apariencia"),
        ["set.language"] = ("Idioma", "Language", "Idioma"),
        ["set.theme"] = ("Tema", "Theme", "Tema"),
        ["set.themeSystem"] = ("Seguir o sistema", "Follow the system", "Seguir el sistema"),
        ["set.themeLight"] = ("Claro", "Light", "Claro"),
        ["set.themeDark"] = ("Escuro", "Dark", "Oscuro"),
        ["set.persistentTitle"] = ("Modo persistente", "Persistent mode", "Modo persistente"),
        ["set.persistentHint"] = ("Deixa este computador sempre pronto para receber.", "Keeps this computer always ready to receive.", "Deja este equipo siempre listo para recibir."),
        ["set.sendingTitle"] = ("Envio", "Sending", "Envío"),
        ["set.connections"] = ("Conexões paralelas", "Parallel connections", "Conexiones paralelas"),
        ["set.connectionsHint"] = ("Mais conexões ajudam com muitos arquivos pequenos.", "More connections help with many small files.", "Más conexiones ayudan con muchos archivos pequeños."),
        ["set.ignore"] = ("Ignorar arquivos (separados por ;)", "Ignore files (separated by ;)", "Ignorar archivos (separados por ;)"),
        ["about.desc"] = ("Copia arquivos entre computadores da rede local, sem compartilhamento do sistema.", "Copies files between computers on the local network, without system file sharing.", "Copia archivos entre equipos de la red local, sin el uso compartido del sistema."),
        ["about.version"] = ("Versão {0}", "Version {0}", "Versión {0}"),
        ["about.license"] = ("© 2026 Ricardo Valério da Silva · Licença MIT", "© 2026 Ricardo Valério da Silva · MIT License", "© 2026 Ricardo Valério da Silva · Licencia MIT"),
        ["dest.default"] = ("Backups de clientes", "Client backups", "Copias de clientes"),
        ["upd.openPage"] = ("Abrir a página de download?", "Open the download page?", "¿Abrir la página de descarga?"),
        ["upd.manual"] = ("Versão {0} disponível (você tem a {1}). Neste sistema a atualização é feita baixando o novo pacote.", "Version {0} is available (you have {1}). On this system, update by downloading the new package.", "La versión {0} está disponible (tienes la {1}). En este sistema se actualiza descargando el paquete nuevo."),
        ["dlg.yes"] = ("Sim", "Yes", "Sí"),
        ["dlg.no"] = ("Não", "No", "No"),
        ["dlg.ok"] = ("OK", "OK", "Aceptar"),

        // ---------------------------------------------------------------- atualização
        ["upd.checking"] = ("Verificando atualizações...", "Checking for updates...", "Buscando actualizaciones..."),
        ["upd.upToDate"] = ("Você já está com a versão mais recente ({0}).", "You already have the latest version ({0}).", "Ya tienes la versión más reciente ({0})."),
        ["upd.available"] = ("Versão {0} disponível (você tem a {1}).\n\nBaixar e atualizar agora? O programa vai reabrir sozinho.",
                             "Version {0} is available (you have {1}).\n\nDownload and update now? The program will reopen by itself.",
                             "La versión {0} está disponible (tienes la {1}).\n\n¿Descargar y actualizar ahora? El programa se reabrirá solo."),
        ["upd.failed"] = ("Não foi possível verificar atualizações:\n{0}", "Could not check for updates:\n{0}", "No se pudieron buscar actualizaciones:\n{0}"),
        ["upd.noAsset"] = ("A versão {0} não tem o NetBackupTool.exe para baixar.", "Version {0} has no NetBackupTool.exe to download.", "La versión {0} no tiene NetBackupTool.exe para descargar."),
        ["upd.downloading"] = ("Baixando a versão {0}... {1}%", "Downloading version {0}... {1}%", "Descargando la versión {0}... {1}%"),
        ["upd.badHash"] = ("O arquivo baixado não confere com o publicado (hash diferente). A atualização foi cancelada.",
                           "The downloaded file does not match the published one (hash mismatch). The update was canceled.",
                           "El archivo descargado no coincide con el publicado (hash distinto). Se canceló la actualización."),
        ["upd.cantReplace"] = ("Não consegui substituir o programa nesta pasta ({0}).\n\nAbrir a página de download para baixar manualmente?",
                               "Could not replace the program in this folder ({0}).\n\nOpen the download page to download it manually?",
                               "No se pudo reemplazar el programa en esta carpeta ({0}).\n\n¿Abrir la página de descarga para bajarlo manualmente?"),
        ["upd.title"] = ("Atualização", "Update", "Actualización"),
    };
}
