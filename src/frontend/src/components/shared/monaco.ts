// Monaco without the language services. `import 'monaco-editor'` pulls in
// the TypeScript, CSS, HTML and JSON services, whose web workers alone were
// about 17 MB of the renderer bundle. WDC only edits server config files and
// SQL, so it needs the editor core (find, folding, multi-cursor, …) plus
// Monarch syntax highlighting for a handful of languages, none of which
// needs a worker beyond the base editor worker.
// monaco-editor >= 0.56 maps its ESM tree to the package root ("./*" ->
// "./esm/vs/*.js"); features/register.all is what edcore.main used to be.
import 'monaco-editor/features/register.all'
import 'monaco-editor/languages/definitions/ini/register'
import 'monaco-editor/languages/definitions/yaml/register'
import 'monaco-editor/languages/definitions/sql/register'
import 'monaco-editor/languages/definitions/mysql/register'
import 'monaco-editor/languages/definitions/pgsql/register'
import 'monaco-editor/languages/definitions/shell/register'
import 'monaco-editor/languages/definitions/bat/register'
import 'monaco-editor/languages/definitions/powershell/register'
import 'monaco-editor/languages/definitions/xml/register'
import 'monaco-editor/languages/definitions/php/register'
import 'monaco-editor/languages/definitions/javascript/register'
import 'monaco-editor/languages/definitions/dockerfile/register'
import 'monaco-editor/languages/definitions/markdown/register'
import editorWorker from 'monaco-editor/editor/editor.worker?worker'

;(self as unknown as Record<string, unknown>).MonacoEnvironment = {
  getWorker: () => new editorWorker(),
}

export * from 'monaco-editor/editor/editor.api'
