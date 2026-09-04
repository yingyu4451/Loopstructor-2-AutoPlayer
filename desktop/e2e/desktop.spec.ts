import { _electron as electron, expect, test } from '@playwright/test'
import { existsSync, mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve } from 'node:path'

const repositoryRoot = resolve(process.cwd(), '..')
const releaseHostPath = resolve(repositoryRoot, 'src/Loopstructor.AutoPlayer.Host/bin/Release/net8.0-windows/Loopstructor.AutoPlayer.Host.exe')
const hostPath = existsSync(releaseHostPath)
  ? releaseHostPath
  : resolve(repositoryRoot, 'src/Loopstructor.AutoPlayer.Host/bin/Debug/net8.0-windows/Loopstructor.AutoPlayer.Host.exe')
const screenshotRoot = resolve(repositoryRoot, 'artifacts/ui/v0.6.69-electron')

test('unified desktop is sandboxed and responsive across every route', async () => {
  const dataRoot = mkdtempSync(resolve(tmpdir(), 'loopstructor-electron-e2e-'))
  mkdirSync(screenshotRoot, { recursive: true })
  const app = await electron.launch({
    args: ['.'],
    cwd: process.cwd(),
    env: {
      ...process.env,
      LOCALAPPDATA: dataRoot,
      LOOPSTRUCTOR_AUTOPLAYER_DESKTOP_USER_DATA_ROOT: dataRoot,
      LOOPSTRUCTOR_AUTOPLAYER_HOST_PATH: hostPath,
      LOOPSTRUCTOR_AUTOPLAYER_HOST_DATA_ROOT: dataRoot,
    },
  })

  try {
    const page = await app.firstWindow()
    const rendererErrors: string[] = []
    page.on('pageerror', (error) => rendererErrors.push(error.message))
    await expect(page.getByText('Loopstructor 2 QA Tool', { exact: true })).toBeVisible()
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

    const routes = ['游戏与插件', '存档', '自动游玩', '战车', '道具', '遗物', '战斗', '对象属性', '生成', '日志与状态', '界面与更新']
    await page.getByRole('button', { name: '战车', exact: true }).click()
    await page.waitForTimeout(1_100)
    await expect(page.locator('.nav-item.active')).toHaveAttribute('aria-label', '战车')
    await expect(page.getByText('开启作弊', { exact: true })).toHaveCount(0)
    await page.getByRole('button', { name: '自动游玩', exact: true }).click()
    await expect(page.getByRole('status').getByText('自动游玩尚未完成', { exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: '开始', exact: true })).toBeVisible()
    await page.getByRole('button', { name: '存档', exact: true }).click()
    await expect(page.getByText('存档保险库', { exact: true })).toBeVisible()
    await expect(page.getByText('自动备份存档', { exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: '打开目录', exact: true })).toBeVisible()
    await page.getByRole('button', { name: '界面与更新', exact: true }).click()
    await page.getByRole('radio', { name: '天穹机械终端' }).click()
    await page.getByRole('button', { name: '应用界面设置', exact: true }).click()
    await expect(page.locator('.app-shell')).toHaveAttribute('data-skin', 'skyspine')
    await page.getByRole('radio', { name: /自定义/ }).check()
    await page.getByRole('slider').fill('125')
    await page.getByRole('button', { name: '应用界面设置', exact: true }).click()
    await page.waitForTimeout(1_200)
    await expect(page.locator('.nav-item.active')).toHaveAttribute('aria-label', '界面与更新')
    expect(await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]?.webContents.getZoomFactor())).toBeCloseTo(1.25, 2)
    await page.getByRole('radio', { name: /跟随系统 DPI/ }).check()
    await page.getByRole('button', { name: '应用界面设置', exact: true }).click()
    await expect(page.locator('.nav-item.active')).toHaveAttribute('aria-label', '界面与更新')
    expect(await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]?.webContents.getZoomFactor())).toBeCloseTo(1, 2)
    await page.emulateMedia({ reducedMotion: 'reduce' })
    await page.waitForTimeout(3_500)
    const views = [
      { width: 980, height: 680, zoom: 1 },
      { width: 1280, height: 860, zoom: 1 },
      { width: 980, height: 680, zoom: 1.25 },
    ]
    for (const view of views) {
      await app.evaluate(({ BrowserWindow }, nextView) => {
        const browserWindow = BrowserWindow.getAllWindows()[0]
        browserWindow?.setSize(nextView.width, nextView.height)
        browserWindow?.webContents.setZoomFactor(nextView.zoom)
      }, view)
      if (view.width === 980) {
        const navLabels = page.locator('.nav-label')
        await expect(navLabels).toHaveCount(routes.length)
        for (let index = 0; index < routes.length; index += 1) {
          await expect(navLabels.nth(index)).toBeVisible()
        }
      }
      await expect(page.locator('.workbench-caption')).toHaveCount(0)
      for (const route of routes) {
        await page.getByRole('button', { name: route, exact: true }).click()
        await expect(page.locator('.nav-item.active')).toHaveAttribute('aria-label', route)
        await page.waitForTimeout(280)
        if (await page.locator('.page-host > *').count() === 0) {
          throw new Error(`${route} 页面没有渲染。${rendererErrors.join('\n')}`)
        }
        await expect(page.locator('.page-host > *')).toBeVisible()
        await expect(page.locator('.cheat-control-bar')).toHaveCount(0)
        await expect(page.getByText('作弊运行控制', { exact: true })).toHaveCount(0)
        const geometry = await page.evaluate(() => {
          const pageHost = document.querySelector<HTMLElement>('.page-host')!
          const hostBounds = pageHost.getBoundingClientRect()
          const clippedSections = Array.from(pageHost.querySelectorAll<HTMLElement>('.mechanical-section'))
            .map((section, index) => {
              const bounds = section.getBoundingClientRect()
              return { index, left: bounds.left, right: bounds.right }
            })
            .filter(bounds => bounds.left < hostBounds.left - 1 || bounds.right > hostBounds.right + 1)
          const clippedSectionContents = Array.from(pageHost.querySelectorAll<HTMLElement>('.mechanical-section'))
            .map((section, index) => ({
              index,
              clientHeight: section.clientHeight,
              scrollHeight: section.scrollHeight,
            }))
            .filter(section => section.scrollHeight > section.clientHeight + 1)
          return {
            clientWidth: pageHost.clientWidth,
            scrollWidth: pageHost.scrollWidth,
            visualViewportWidth: window.visualViewport?.width ?? window.innerWidth,
            documentClientWidth: document.documentElement.clientWidth,
            documentScrollWidth: document.documentElement.scrollWidth,
            hostLeft: hostBounds.left,
            hostRight: hostBounds.right,
            clippedSections,
            clippedSectionContents,
          }
        })
        expect(
          geometry.documentScrollWidth,
          `${route} 在 ${view.width}×${view.height} / ${view.zoom * 100}% 下使应用壳超出 viewport`,
        ).toBeLessThanOrEqual(geometry.documentClientWidth + 1)
        expect(geometry.hostLeft).toBeGreaterThanOrEqual(-1)
        expect(
          geometry.hostRight,
          `${route} 在 ${view.width}×${view.height} / ${view.zoom * 100}% 下使页面内容区越出 viewport`,
        ).toBeLessThanOrEqual(geometry.visualViewportWidth + 1)
        expect(
          geometry.scrollWidth,
          `${route} 在 ${view.width}×${view.height} / ${view.zoom * 100}% 下发生横向溢出`,
        ).toBeLessThanOrEqual(geometry.clientWidth + 1)
        expect(
          geometry.clippedSections,
          `${route} 在 ${view.width}×${view.height} / ${view.zoom * 100}% 下存在被裁切的机械边框`,
        ).toEqual([])
        expect(
          geometry.clippedSectionContents,
          `${route} 在 ${view.width}×${view.height} / ${view.zoom * 100}% 下存在溢出机械边框的内容`,
        ).toEqual([])
        const safeName = route.replaceAll('/', '-')
        const zoomSuffix = view.zoom === 1 ? '' : `-${view.zoom * 100}pct`
        const screenshotBase64 = await app.evaluate(async ({ BrowserWindow }) =>
          (await BrowserWindow.getAllWindows()[0]!.webContents.capturePage()).toPNG().toString('base64'))
        writeFileSync(
          resolve(screenshotRoot, `${view.width}x${view.height}${zoomSuffix}-${safeName}.png`),
          Buffer.from(screenshotBase64, 'base64'),
        )
      }
      if (view.zoom === 1) {
        await page.getByRole('button', { name: '道具', exact: true }).click()
        const itemLayout = await page.evaluate(() => {
          const workspace = document.querySelector<HTMLElement>('.items-workspace')!
          const workspaceBounds = workspace.getBoundingClientRect()
          const sections = Array.from(workspace.querySelectorAll<HTMLElement>(':scope > .inventory-column'))
          const sectionBounds = sections.map(section => section.getBoundingClientRect())
          return {
            overflowY: getComputedStyle(workspace).overflowY,
            clientHeight: workspace.clientHeight,
            scrollHeight: workspace.scrollHeight,
            topOffsets: sectionBounds.map(bounds => Math.abs(bounds.top - workspaceBounds.top)),
            bottomOffsets: sectionBounds.map(bounds => Math.abs(bounds.bottom - workspaceBounds.bottom)),
          }
        })
        if (view.width === 1280) {
          expect(itemLayout.topOffsets.every(offset => offset <= 1)).toBe(true)
          expect(itemLayout.bottomOffsets.every(offset => offset <= 1)).toBe(true)
        } else {
          expect(itemLayout.overflowY).toBe('auto')
          expect(itemLayout.scrollHeight).toBeGreaterThan(itemLayout.clientHeight)
        }
      }
    }
    expect(rendererErrors).toEqual([])
  } finally {
    await app.close()
    rmSync(dataRoot, { recursive: true, force: true })
  }
})

test('reuses the Electron runtime for the updater mode', async () => {
  const dataRoot = mkdtempSync(resolve(tmpdir(), 'loopstructor-electron-updater-e2e-'))
  const readyRoot = resolve(tmpdir(), 'LoopstructorAutoPlayerUpdater')
  const readyFile = resolve(readyRoot, `ready-e2e-${process.pid}.signal`)
  mkdirSync(readyRoot, { recursive: true })
  rmSync(readyFile, { force: true })
  const app = await electron.launch({
    args: ['.', '--updater', '--window-ready-file', readyFile, '--desktop-staged-run'],
    cwd: process.cwd(),
    env: {
      ...process.env,
      LOCALAPPDATA: dataRoot,
      LOOPSTRUCTOR_AUTOPLAYER_DESKTOP_USER_DATA_ROOT: dataRoot,
    },
  })

  try {
    const page = await app.firstWindow()
    await expect.poll(() => existsSync(readyFile)).toBe(true)
    await expect(page.getByText('更新未完成', { exact: true })).toBeVisible()
    expect(await page.evaluate(() => window.loopstructorDesktop.isUpdater)).toBe(true)
    await expect(page.locator('.updater-message')).toHaveText('apply 命令必须提供 --target <release-root>。')
    for (const size of [{ width: 680, height: 520 }, { width: 760, height: 600 }]) {
      await app.evaluate(({ BrowserWindow }, nextSize) => {
        BrowserWindow.getAllWindows()[0]?.setSize(nextSize.width, nextSize.height)
      }, size)
      await page.waitForTimeout(100)
      const layout = await page.evaluate(() => {
        const appRoot = document.querySelector<HTMLElement>('#app')!
        const card = document.querySelector<HTMLElement>('.updater-card')!
        const bounds = card.getBoundingClientRect()
        return {
          rootWidth: appRoot.clientWidth,
          rootScrollWidth: appRoot.scrollWidth,
          rootHeight: appRoot.clientHeight,
          rootScrollHeight: appRoot.scrollHeight,
          left: bounds.left,
          top: bounds.top,
          right: bounds.right,
          bottom: bounds.bottom,
        }
      })
      expect(layout.rootScrollWidth).toBeLessThanOrEqual(layout.rootWidth)
      expect(layout.rootScrollHeight).toBeLessThanOrEqual(layout.rootHeight)
      expect(layout.left).toBeGreaterThanOrEqual(0)
      expect(layout.top).toBeGreaterThanOrEqual(54)
      expect(layout.right).toBeLessThanOrEqual(layout.rootWidth)
      expect(layout.bottom).toBeLessThanOrEqual(layout.rootHeight)
      await page.screenshot({
        path: resolve(screenshotRoot, `${size.width}x${size.height}-更新窗口.png`),
        animations: 'disabled',
      })
    }
    await expect(page.getByRole('button', { name: '退出', exact: true })).toBeVisible()
  } finally {
    await app.close()
    rmSync(dataRoot, { recursive: true, force: true })
    rmSync(readyFile, { force: true })
  }
})

test('skyspine interaction states preserve material, focus, and chrome spacing', async () => {
  const dataRoot = mkdtempSync(resolve(tmpdir(), 'loopstructor-electron-states-e2e-'))
  mkdirSync(screenshotRoot, { recursive: true })
  const app = await electron.launch({
    args: ['.'],
    cwd: process.cwd(),
    env: {
      ...process.env,
      LOCALAPPDATA: dataRoot,
      LOOPSTRUCTOR_AUTOPLAYER_DESKTOP_USER_DATA_ROOT: dataRoot,
      LOOPSTRUCTOR_AUTOPLAYER_HOST_PATH: hostPath,
      LOOPSTRUCTOR_AUTOPLAYER_HOST_DATA_ROOT: dataRoot,
    },
  })

  try {
    const page = await app.firstWindow()
    await expect(page.getByText('Loopstructor 2 QA Tool', { exact: true })).toBeVisible()
    await expect(page.locator('.titlebar-status')).not.toContainText('正在启动 Host', { timeout: 15_000 })
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]?.setSize(1280, 860))
    await page.waitForTimeout(250)

    const activeNav = page.locator('.nav-item.active')
    const navBefore = await activeNav.evaluate(element => ({
      face: getComputedStyle(element, '::before').backgroundImage,
      filter: getComputedStyle(element).filter,
      shadow: getComputedStyle(element).boxShadow,
    }))
    await activeNav.hover()
    await page.waitForTimeout(180)
    const navHover = await activeNav.evaluate(element => ({
      face: getComputedStyle(element, '::before').backgroundImage,
      filter: getComputedStyle(element).filter,
      cog: getComputedStyle(element.querySelector('.rail-cog')!).backgroundImage,
    }))
    expect(navBefore.face).not.toBe('none')
    expect(navBefore.shadow).not.toContain('inset')
    expect(navHover.face).not.toBe('none')
    expect(navHover.cog).toContain('gear-')
    expect(navHover.filter).not.toBe(navBefore.filter)
    await page.screenshot({ path: resolve(screenshotRoot, '1280x860-state-nav-hover.png'), animations: 'disabled' })

    const secondary = page.getByRole('button', { name: '刷新连接', exact: true })
    const secondaryBefore = await secondary.evaluate(element => getComputedStyle(element, '::before').backgroundImage)
    await secondary.hover()
    const secondaryHover = await secondary.evaluate(element => ({
      face: getComputedStyle(element, '::before').backgroundImage,
      filter: getComputedStyle(element).filter,
    }))
    expect(secondaryBefore).not.toBe('none')
    expect(secondaryHover.face).not.toBe('none')
    expect(secondaryHover.filter).toContain('brightness')
    await secondary.focus()
    expect(await secondary.evaluate(element => getComputedStyle(element).boxShadow)).not.toBe('none')

    const assertDarkPrimaryButtons = async () => {
      const primaryButtons = page.locator('.button.primary:not(:disabled)')
      expect(await primaryButtons.count()).toBeGreaterThan(0)
      for (let index = 0; index < await primaryButtons.count(); index += 1) {
        const palette = await primaryButtons.nth(index).evaluate(element => ({
          color: getComputedStyle(element).color,
          edge: getComputedStyle(element).getPropertyValue('--button-edge').trim(),
          top: getComputedStyle(element).getPropertyValue('--button-top').trim(),
          mid: getComputedStyle(element).getPropertyValue('--button-mid').trim(),
          bottom: getComputedStyle(element).getPropertyValue('--button-bottom').trim(),
          face: getComputedStyle(element, '::before').backgroundImage,
        }))
        expect(palette).toMatchObject({
          color: 'rgb(255, 227, 162)',
          edge: '#d69a45',
          top: '#6a3d1d',
          mid: '#3a2111',
          bottom: '#1d1009',
        })
        expect(palette.face).not.toBe('none')
      }
    }

    await assertDarkPrimaryButtons()
    const directoryButton = page.getByRole('button', { name: '选择目录', exact: true })
    const directoryRest = await directoryButton.evaluate(element => ({
      color: getComputedStyle(element).color,
      edge: getComputedStyle(element).getPropertyValue('--button-edge').trim(),
      face: getComputedStyle(element, '::before').backgroundImage,
    }))
    expect(directoryRest.color).toBe('rgb(255, 227, 162)')
    expect(directoryRest.edge).toBe('#d69a45')
    expect(directoryRest.face).not.toBe('none')
    await directoryButton.hover()
    await expect.poll(
      () => directoryButton.evaluate(element => getComputedStyle(element).color),
      { message: '选择目录按钮的 hover 颜色应完成过渡' },
    ).toBe('rgb(255, 240, 198)')

    const disabledLaunch = page.getByRole('button', { name: '启动游戏', exact: true })
    await expect(disabledLaunch).toBeDisabled()
    const disabledMaterial = await disabledLaunch.evaluate(element => ({
      face: getComputedStyle(element, '::before').backgroundImage,
      background: getComputedStyle(element).backgroundColor,
      shadow: getComputedStyle(element).boxShadow,
    }))
    expect(disabledMaterial.face).not.toBe('none')
    expect(disabledMaterial.background).not.toBe('rgba(0, 0, 0, 0)')
    expect(disabledMaterial.shadow).not.toBe('none')
    await page.screenshot({ path: resolve(screenshotRoot, '1280x860-state-button-focus-disabled.png'), animations: 'disabled' })

    const chromeSpacing = await page.evaluate(() => {
      const pageHost = document.querySelector<HTMLElement>('.page-host')!.getBoundingClientRect()
      const heading = document.querySelector<HTMLElement>('.page-host > *')!.getBoundingClientRect()
      const chrome = getComputedStyle(document.querySelector<HTMLElement>('.content-shell')!, '::before')
      return { contained: pageHost.top <= heading.top, chromeZ: Number(chrome.zIndex), chromeMask: chrome.maskImage || chrome.webkitMaskImage }
    })
    expect(chromeSpacing.contained).toBe(true)
    expect(chromeSpacing.chromeZ).toBeGreaterThan(2)
    expect(chromeSpacing.chromeMask).not.toBe('none')

    await page.getByRole('button', { name: '界面与更新', exact: true }).click()
    await assertDarkPrimaryButtons()
    const selectedSkin = page.getByRole('radio', { name: '天穹机械终端' })
    await selectedSkin.hover()
    expect(await selectedSkin.evaluate(element => getComputedStyle(element, '::before').backgroundImage)).not.toBe('none')
    expect(await selectedSkin.evaluate(element => getComputedStyle(element).getPropertyValue('--skin-edge').trim())).toBe('#a0f36f')
    expect(await selectedSkin.evaluate(element => getComputedStyle(element, '::before').boxShadow)).not.toContain('7px 0px')
    await page.screenshot({ path: resolve(screenshotRoot, '1280x860-state-selected-hover.png'), animations: 'disabled' })

    await page.getByRole('button', { name: '战车', exact: true }).click()
    const activeTab = page.locator('.type-switcher button.active')
    await activeTab.hover()
    expect(await activeTab.evaluate(element => getComputedStyle(element).backgroundColor)).not.toBe('rgba(0, 0, 0, 0)')
    await expect(page.locator('.cheat-control-bar')).toHaveCount(0)
    await page.screenshot({ path: resolve(screenshotRoot, '1280x860-state-tab-hover.png'), animations: 'disabled' })

    const closeButton = page.getByRole('button', { name: '关闭', exact: true })
    await closeButton.hover()
    expect(await closeButton.evaluate(element => getComputedStyle(element).backgroundImage)).toContain('gear-')
  } finally {
    await app.close()
    rmSync(dataRoot, { recursive: true, force: true })
  }
})
