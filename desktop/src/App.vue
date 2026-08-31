<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'
import TitleBar from './components/TitleBar.vue'
import RailSidebar from './components/RailSidebar.vue'
import CheatControlBar from './components/CheatControlBar.vue'
import AppToast from './components/AppToast.vue'
import AppModal from './components/AppModal.vue'
import GamePage from './pages/GamePage.vue'
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
import { useAppStore } from './stores/app'
import { useUiStore } from './stores/ui'

const store = useAppStore()
const ui = useUiStore()
const updaterMode = window.loopstructorDesktop.isUpdater === true
const pages = {
  game: GamePage, autoplay: AutoplayPage, vehicles: VehiclesPage, items: ItemsPage,
  relics: RelicsPage, battle: BattlePage, objects: ObjectsPage, spawn: SpawnPage,
  diagnostics: DiagnosticsPage, settings: SettingsPage,
}
const activePage = computed(() => pages[store.route])
const cheatPage = computed(() => ['vehicles', 'items', 'relics', 'battle', 'objects', 'spawn'].includes(store.route))

onMounted(async () => {
  if (updaterMode) return
  await store.initialize()
  const settings = store.settings
  if (settings?.uiScaleMode === 'custom') await window.loopstructorDesktop.setZoom(settings.customUiScalePercent / 100)
})
onBeforeUnmount(() => store.removeHostEvent?.())
</script>

<template>
  <div class="app-shell">
    <TitleBar :updater-mode="updaterMode" />
    <UpdaterPage v-if="updaterMode" />
    <div v-else class="app-body" :class="{ 'sidebar-collapsed': store.settings?.sidebarCollapsed }">
      <RailSidebar />
      <main class="content-shell">
        <CheatControlBar v-if="cheatPage" />
        <div class="page-host" :class="{ 'with-cheat-bar': cheatPage }">
          <component :is="activePage" />
        </div>
      </main>
    </div>
    <AppToast />
    <AppModal />
    <div v-if="ui.busy" class="busy-indicator" aria-label="正在处理"><span /><span /><span /></div>
  </div>
</template>
