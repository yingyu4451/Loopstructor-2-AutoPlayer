<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, watch } from 'vue'
import { gsap } from 'gsap'
import TitleBar from './components/TitleBar.vue'
import RailSidebar from './components/RailSidebar.vue'
import AppToast from './components/AppToast.vue'
import AppModal from './components/AppModal.vue'
import GamePage from './pages/GamePage.vue'
import SavesPage from './pages/SavesPage.vue'
import AutoplayPage from './pages/AutoplayPage.vue'
import VehiclesPage from './pages/VehiclesPage.vue'
import ItemsPage from './pages/ItemsPage.vue'
import RelicsPage from './pages/RelicsPage.vue'
import BattlePage from './pages/BattlePage.vue'
import ObjectsPage from './pages/ObjectsPage.vue'
import SpawnPage from './pages/SpawnPage.vue'
import DiagnosticsPage from './pages/DiagnosticsPage.vue'
import SettingsPage from './pages/SettingsPage.vue'
import UpdaterPage from './pages/UpdaterPage.vue'
import stationArt from './assets/skins/skyspine/station.png'
import { useAppStore } from './stores/app'
import { useUiStore } from './stores/ui'

const store = useAppStore()
const ui = useUiStore()
const updaterMode = window.loopstructorDesktop.isUpdater === true
const pages = {
  game: GamePage, saves: SavesPage, autoplay: AutoplayPage, vehicles: VehiclesPage, items: ItemsPage,
  relics: RelicsPage, battle: BattlePage, objects: ObjectsPage, spawn: SpawnPage,
  diagnostics: DiagnosticsPage, settings: SettingsPage,
}
const activePage = computed(() => pages[store.route])
let pageTween: gsap.core.Tween | undefined
let busyTween: gsap.core.Tween | undefined

function prefersReducedMotion(): boolean {
  return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true
}

async function animatePage(): Promise<void> {
  await nextTick()
  const target = document.querySelector('.page-host > *')
  if (!target || prefersReducedMotion()) return
  pageTween?.kill()
  pageTween = gsap.fromTo(target, { opacity: 0, y: 8 }, {
    opacity: 1,
    y: 0,
    duration: 0.24,
    ease: 'power2.out',
    clearProps: 'transform',
  })
  gsap.to('.title-cog, .nav-item.active .rail-cog', {
    rotation: '+=45',
    duration: 0.34,
    stagger: 0.035,
    ease: 'power2.out',
  })
}

watch(() => store.route, animatePage)
watch(() => ui.busy, (busy) => {
  if (prefersReducedMotion()) return
  busyTween?.kill()
  busyTween = busy
    ? gsap.fromTo('.busy-indicator', { opacity: 0, y: 6 }, { opacity: 1, y: 0, duration: 0.2, ease: 'power2.out' })
    : gsap.to('.busy-indicator', { opacity: 0, y: 6, duration: 0.16, ease: 'power1.in' })
})

onMounted(async () => {
  if (updaterMode) return
  await store.initialize()
  const settings = store.settings
  if (settings?.uiScaleMode === 'custom') await window.loopstructorDesktop.setZoom(settings.customUiScalePercent / 100)
})
onBeforeUnmount(() => {
  store.removeHostEvent?.()
  pageTween?.kill()
  busyTween?.kill()
})
</script>

<template>
  <div class="app-shell h-screen min-h-0 overflow-hidden" data-theme="skyspine" :data-skin="store.settings?.skinId ?? 'skyspine'" :data-route="store.route" :class="{ 'updater-mode': updaterMode }">
    <TitleBar :updater-mode="updaterMode" />
    <UpdaterPage v-if="updaterMode" />
    <div v-else class="app-body grid min-h-0" :class="{ 'sidebar-collapsed': store.settings?.sidebarCollapsed }">
      <RailSidebar />
      <main class="content-shell relative min-h-0 min-w-0 overflow-hidden">
        <img :src="stationArt" alt="" width="256" height="256" class="workspace-station" aria-hidden="true" />
        <div class="page-host h-full min-h-0 min-w-0 overflow-hidden">
          <component :is="activePage" />
        </div>
      </main>
    </div>
    <AppToast />
    <AppModal />
    <div v-if="ui.busy" class="busy-indicator" aria-label="正在处理"><span /><span /><span /></div>
  </div>
</template>
