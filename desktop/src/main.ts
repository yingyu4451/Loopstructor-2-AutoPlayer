import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { tooltipDirective } from './directives/tooltip'
import './styles.css'
import './skyspine.css'

const app = createApp(App)
app.use(createPinia())
app.directive('tooltip', tooltipDirective)
app.mount('#app')
