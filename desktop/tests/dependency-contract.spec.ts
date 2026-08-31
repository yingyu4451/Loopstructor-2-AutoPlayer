import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

describe('desktop dependency contract', () => {
  it('uses offline Iconify assets and does not include Lucide', () => {
    const packageJson = JSON.parse(readFileSync(resolve(process.cwd(), 'package.json'), 'utf8'))
    expect(packageJson.dependencies['@iconify/vue']).toBeTruthy()
    expect(packageJson.dependencies['@iconify-icons/mdi']).toBeTruthy()
    expect(packageJson.dependencies['lucide-vue-next']).toBeUndefined()
    expect(packageJson.dependencies['@lucide/vue']).toBeUndefined()
  })
})
