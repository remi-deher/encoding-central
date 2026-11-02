namespace Encoding.NodeAgent.Runtime;

/// <summary>
/// Aide <c>tools</c> injectée dans les scripts <c>base-script</c> qui demandent <c>useTools</c> (architecture 0054).
/// Le script lance les outils média lui-même, dans le bac à sable Landlock du script : la liste blanche et la
/// résolution des exécutables sont une commodité, la frontière de sécurité reste Landlock (écriture limitée à l'espace
/// temporaire, aucun accès aux montages média).
/// </summary>
internal static class ScriptTools
{
    /// <summary>Capacité annoncée par un worker dont l'hôte de scripts fournit l'aide <c>tools</c>.</summary>
    public const string CapabilityId = "script-tools";
    public const string CapabilityVersion = "1";

    /// <summary>Outils appelables par nom. Identiques pour JavaScript et Python.</summary>
    public static readonly string[] AllowedTools = ["ffmpeg", "ffprobe", "mkvmerge", "mkvextract", "mkvpropedit", "mediainfo", "magick", "codex", "claude"];

    /// <summary>Durée maximale par défaut d'une commande, en secondes ; la durée du script reste bornée par timeoutSeconds.</summary>
    public const int DefaultToolTimeoutSeconds = 21600;

    private static string AllowedJson => System.Text.Json.JsonSerializer.Serialize(AllowedTools);

    public static string Javascript => """
        const tools = (() => {
          const cp = require('child_process'), path = require('path'), fs = require('fs');
          const allowed = __ALLOWED__;
          const cwd = process.cwd();
          const extensions = process.platform === 'win32' ? (process.env.PATHEXT || '.EXE;.BAT;.CMD').split(';').map(e => e.toLowerCase()) : [''];
          const find = name => {
            const hasExt = path.extname(name) !== '';
            const names = hasExt || process.platform !== 'win32' ? [name] : [name, ...extensions.map(ext => name + ext)];
            for (const dir of (process.env.PATH || '').split(path.delimiter)) {
              if (!dir) continue;
              for (const n of names) {
                const candidate = path.join(dir, n);
                try {
                  if (fs.statSync(candidate).isFile()) {
                    if (process.platform !== 'win32') fs.accessSync(candidate, fs.constants.X_OK);
                    return candidate;
                  }
                } catch (e) { }
              }
            }
            return null;
          };
          const tail = (text, limit) => text.length > limit ? text.slice(text.length - limit) : text;
          return Object.freeze({
            allowed: Object.freeze(allowed.slice()),
            path(name) { return allowed.includes(name) ? find(name) : null; },
            run(name, args = [], options = {}) {
              if (!allowed.includes(name)) throw new Error('Outil non autorisé : ' + name + '. Outils disponibles : ' + allowed.join(', ') + '.');
              if (!Array.isArray(args) || args.some(a => typeof a !== 'string' && typeof a !== 'number')) throw new Error('Les arguments doivent être une liste de chaînes.');
              const executable = find(name);
              if (!executable) throw new Error('Outil indisponible sur ce nœud : ' + name + '.');
              const seconds = Math.max(1, Number(options.timeoutSeconds ?? __TIMEOUT__));
              const started = Date.now();
              const r = cp.spawnSync(executable, args.map(String), { cwd, input: options.input, encoding: 'utf8', shell: false, timeout: seconds * 1000, maxBuffer: 64 * 1024 * 1024, killSignal: 'SIGKILL' });
              if (r.error) throw new Error(name + ' : ' + (r.error.code === 'ETIMEDOUT' ? 'durée maximale dépassée (' + seconds + ' s)' : r.error.message));
              const exitCode = r.status === null ? -1 : r.status;
              process.stderr.write('[tools] ' + name + ' ' + args.map(String).join(' ').slice(0, 400) + ' -> ' + exitCode + ' (' + (Date.now() - started) + ' ms)\n');
              // mkvmerge, mkvextract et mkvpropedit retournent 1 pour de simples avertissements.
              const ok = exitCode === 0 || (exitCode === 1 && name.startsWith('mkv'));
              return { exitCode, ok, stdout: tail(r.stdout || '', 1048576), stderr: tail(r.stderr || '', 262144) };
            }
          });
        })();

        """.Replace("__ALLOWED__", AllowedJson).Replace("__TIMEOUT__", DefaultToolTimeoutSeconds.ToString());

    public static string Python => """
        import subprocess as _sp, os as _os, time as _time, sys as _sys, shutil as _shutil
        class _Tools:
            allowed = tuple(__ALLOWED__)
            def _find(self, name):
                return _shutil.which(name)
            def path(self, name):
                return self._find(name) if name in self.allowed else None
            def run(self, name, args=(), timeout_seconds=__TIMEOUT__, input=None):
                if name not in self.allowed:
                    raise RuntimeError('Outil non autorisé : %s. Outils disponibles : %s.' % (name, ', '.join(self.allowed)))
                args = list(args)
                if any(not isinstance(a, (str, int, float)) for a in args):
                    raise RuntimeError('Les arguments doivent être une liste de chaînes.')
                executable = self._find(name)
                if executable is None:
                    raise RuntimeError('Outil indisponible sur ce nœud : %s.' % name)
                started = _time.time()
                try:
                    r = _sp.run([executable] + [str(a) for a in args], cwd=_os.getcwd(), input=input, capture_output=True, text=True, errors='replace', timeout=max(1, float(timeout_seconds)), shell=False)
                except _sp.TimeoutExpired:
                    raise RuntimeError('%s : durée maximale dépassée (%s s)' % (name, timeout_seconds))
                _sys.stderr.write('[tools] %s %s -> %d (%d ms)\n' % (name, ' '.join(str(a) for a in args)[:400], r.returncode, int((_time.time() - started) * 1000)))
                # mkvmerge, mkvextract et mkvpropedit retournent 1 pour de simples avertissements.
                ok = r.returncode == 0 or (r.returncode == 1 and name.startswith('mkv'))
                return {'exitCode': r.returncode, 'ok': ok, 'stdout': (r.stdout or '')[-1048576:], 'stderr': (r.stderr or '')[-262144:]}
        tools = _Tools()

        """.Replace("__ALLOWED__", AllowedJson).Replace("__TIMEOUT__", DefaultToolTimeoutSeconds.ToString());

    /// <summary>Compose le programme complet d'un script ; sans <paramref name="useTools"/>, aucune aide n'est ajoutée et le comportement existant est inchangé.</summary>
    public static string Compose(bool python, bool useTools, string code)
    {
        var prelude = useTools ? (python ? Python : Javascript) : "";
        return python
            ? "import sys,json\ncontext=json.load(sys.stdin)\nvariables=context['variables']\n" + prelude
              + "def run(context):\n" + string.Join('\n', code.Split('\n').Select(line => "    " + line))
              + "\nresult=run(context)\nprint(json.dumps(result if result is not None else {'outputId':'success','variables':variables}))\n"
            : "const fs=require('fs'); const context=JSON.parse(fs.readFileSync(0,'utf8')); const variables=context.variables;\n" + prelude
              + "(async()=>{ const result=await (async()=>{\n" + code
              + "\n})(); process.stdout.write(JSON.stringify(result ?? {outputId:'success',variables})); })().catch(e=>{ process.stderr.write(String(e&&e.stack||e)); process.exit(1); });";
    }
}
