import { _electron as electron, expect, test } from '@playwright/test'
import { existsSync, mkdtempSync, mkdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve } from 'node:path'

const repositoryRoot = resolve(process.cwd(), '..')
const releaseHostPath = resolve(repositoryRoot, 'src/Loopstructor.AutoPlayer.Host/bin/Release/net8.0-windows/Loopstructor.AutoPlayer.Host.exe')
const hostPath = existsSync(releaseHostPath)
  ? releaseHostPath
  : resolve(repositoryRoot, 'src/Loopstructor.AutoPlayer.Host/bin/Debug/net8.0-windows/Loopstructor.AutoPlayer.Host.exe')
const screenshotRoot = resolve(repositoryRoot, 'artifacts/ui/v0.6.51-electron')

test('unified desktop is sandboxed and responsive across every route', async () => {
  const dataRoot = mkdtempSync(resolve(tmpdir(), 'loopstructor-electron-e2e-'))
  mkdirSync(screenshotRoot, { recursive: true })
  const app = await electron.launch({
    args: ['.'],
    cwd: process.cwd(),
    env: {
      ...process.env,
      LOCALAPPDATA: dataRoot,
      LOOPSTRUCTOR_AUTOPLAYER_HOST_PATH: hostPath,
      LOOPSTRUCTOR_AUTOPLAYER_HOST_DATA_ROOT: dataRoot,
    },
  })

  try {
    const page = await app.firstWindow()
    const rendererErrors: string[] = []
    page.on('pageerror', (error) => rendererErrors.push(error.message))
    await expect(page.getByText('Loopstructor AutoPlayer', { exact: true })).toBeVisible()
    expect(await page.evaluate(() => typeof (globalThis as { require?: unknown }).require)).toBe('undefined')
    const directSnapshot = await page.evaluate(async () => {
      try {
        return { value: await window.loopstructorDesktop.getSnapshot(), error: '' }
      } catch (error) {
        return { value: null, error: error instanceof Error ? error.message : String(error) }
      }
    })
    expect(directSnapshot.error, rendererErrors.join('\n')).toBe('')
    expect(directSnapshot.value?.protocolVersion).toBe(1)
    await expect(page.locator('.titlebar-status')).not.toContainText('正在启动 Host', { timeout: 15_000 })

    const routes = ['游戏与插件', '自动游玩', '战车', '道具', '遗物', '战斗', '对象属性', '生成', '日志与状态', '界面与更新']
    for (const size of [{ width: 980, height: 680 }, { width: 1280, height: 860 }]) {
      await app.evaluate(({ BrowserWindow }, nextSize) => {
        BrowserWindow.getAllWindows()[0]?.setSize(nextSize.width, nextSize.height)
      }, size)
      for (const route of routes) {
        await page.getByRole('button', { name: route, exact: true }).click()
        await expect(page.locator('.nav-item.active')).toHaveAttribute('aria-label', route)
        await page.waitForTimeout(50)
        if (await page.locator('.page-host > *').count() === 0) {
          throw new Error(`${route} 页面没有渲染。${rendererErrors.join('\n')}`)
        }
        await expect(page.locator('.page-host > *')).toBeVisible()
        const safeName = route.replaceAll('/', '-')
        await page.screenshot({
          path: resolve(screenshotRoot, `${size.width}x${size.height}-${safeName}.png`),
          animations: 'disabled',
        })
      }
    }
    expect(rendererErrors).toEqual([])
  } finally {
    await app.close()
    rmSync(dataRoot, { recursive: true, force: true })
  }
})
