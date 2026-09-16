/**
 * Stages the bundled harness plugin as a publishable npm package.
 *
 * The plugin has two ways of reaching a home: it ships inside the executable,
 * which is how the app installs it, or it can be installed from npm like any
 * other DSH plugin. The repository copy is `private: true` so a stray publish
 * from a checkout cannot push it, and it carries the app's own files rather
 * than a published payload - so publishing means staging a copy instead of
 * publishing that file.
 *
 * What is staged is exactly what the executable carries: the same manifest,
 * minus `private`, with the two things npm needs that a bundled copy does not -
 * a tarball that stays inside `files`, and a version a consumer can resolve.
 * The two distributions cannot drift because both start from this file.
 *
 * Usage: node tools/stage-plugin-package.mjs <plugin-dir> <out-dir>
 * Exit codes: 0 staged, 1 the manifest cannot be published, 2 the arguments are wrong.
 */

import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

const [sourceArg, outArg] = process.argv.slice(2)
if (!sourceArg || !outArg) {
  console.error('usage: node tools/stage-plugin-package.mjs <plugin-dir> <out-dir>')
  process.exit(2)
}

const source = resolve(sourceArg)
const out = resolve(outArg)

const manifest = JSON.parse(readFileSync(join(source, 'package.json'), 'utf8'))
const problems = []
if (!manifest.name) problems.push('the manifest has no name')
if (!manifest.version) problems.push('the manifest has no version')
if (!Array.isArray(manifest.files) || manifest.files.length === 0) {
  problems.push('the manifest lists no files, so the tarball would be empty')
}
if (problems.length > 0) {
  for (const problem of problems) console.error('cannot publish: ' + problem)
  process.exit(1)
}

// The published manifest is the bundled one without the two fields that mean
// "not for a registry" and with the entry points npm expects to resolve.
const staged = { ...manifest }
delete staged.private
delete staged.devDependencies

rmSync(out, { recursive: true, force: true })
mkdirSync(out, { recursive: true })

for (const entry of manifest.files) {
  cpSync(join(source, entry), join(out, entry), { recursive: true })
}
writeFileSync(join(out, 'package.json'), JSON.stringify(staged, null, 2) + '\n')

console.log(`${staged.name}@${staged.version} staged in ${out}`)
console.log('files: ' + manifest.files.join(', '))
