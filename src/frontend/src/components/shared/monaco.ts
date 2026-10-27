// Monaco without the language services. `import 'monaco-editor'` pulls in
// the TypeScript, CSS, HTML and JSON services, whose web workers alone were
// about 17 MB of the renderer bundle. WDC only edits server config files and
// SQL, so it needs the editor core (find, folding, multi-cursor, …) plus
// Monarch syntax highlighting for a handful of languages, none of which
// needs a worker beyond the base editor worker.
import 'monaco-editor/esm/vs/editor/edcore.main.js'
import 'monaco-editor/esm/vs/basic-languages/ini/ini.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/yaml/yaml.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/sql/sql.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/mysql/mysql.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/pgsql/pgsql.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/shell/shell.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/bat/bat.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/powershell/powershell.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/xml/xml.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/php/php.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/javascript/javascript.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/dockerfile/dockerfile.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/markdown/markdown.contribution.js'
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker'

;(self as unknown as Record<string, unknown>).MonacoEnvironment = {
  getWorker: () => new editorWorker(),
}

export * from 'monaco-editor/esm/vs/editor/editor.api.js'
