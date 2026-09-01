import { defineComponent, h, type PropType } from 'vue'
import { Icon, type IconifyIcon } from '@iconify/vue'
import minusIcon from '@iconify-icons/mdi/minus'
import squareOutlineIcon from '@iconify-icons/mdi/square-outline'
import closeIcon from '@iconify-icons/mdi/close'
import refreshIcon from '@iconify-icons/mdi/refresh'
import gamepadIcon from '@iconify-icons/mdi/gamepad-variant'
import robotIcon from '@iconify-icons/mdi/robot'
import trainIcon from '@iconify-icons/mdi/train'
import packageIcon from '@iconify-icons/mdi/package-variant'
import diamondIcon from '@iconify-icons/mdi/diamond-stone'
import swordsIcon from '@iconify-icons/mdi/sword-cross'
import scanIcon from '@iconify-icons/mdi/crosshairs-gps'
import bugPlayIcon from '@iconify-icons/mdi/bug-play'
import activityIcon from '@iconify-icons/mdi/chart-timeline-variant'
import cogIcon from '@iconify-icons/mdi/cog'
import chevronLeftIcon from '@iconify-icons/mdi/chevron-left'
import chevronRightIcon from '@iconify-icons/mdi/chevron-right'
import shieldAlertIcon from '@iconify-icons/mdi/shield-alert'
import imageOffIcon from '@iconify-icons/mdi/image-off'
import checkIcon from '@iconify-icons/mdi/check'
import checkCircleIcon from '@iconify-icons/mdi/check-circle'
import infoIcon from '@iconify-icons/mdi/information'
import alertIcon from '@iconify-icons/mdi/alert'
import closeCircleIcon from '@iconify-icons/mdi/close-circle'
import folderOpenIcon from '@iconify-icons/mdi/folder-open'
import shieldCheckIcon from '@iconify-icons/mdi/shield-check'
import plugIcon from '@iconify-icons/mdi/power-plug'
import plugOffIcon from '@iconify-icons/mdi/power-plug-off'
import deleteIcon from '@iconify-icons/mdi/delete'
import playIcon from '@iconify-icons/mdi/play'
import pauseIcon from '@iconify-icons/mdi/pause'
import stopIcon from '@iconify-icons/mdi/stop-circle'
import searchIcon from '@iconify-icons/mdi/magnify'
import plusIcon from '@iconify-icons/mdi/plus'
import mapPinIcon from '@iconify-icons/mdi/map-marker'
import downloadIcon from '@iconify-icons/mdi/download'
import shieldIcon from '@iconify-icons/mdi/shield'
import mapIcon from '@iconify-icons/mdi/map'
import eyeIcon from '@iconify-icons/mdi/eye'
import sparkleIcon from '@iconify-icons/mdi/auto-fix'
import fastForwardIcon from '@iconify-icons/mdi/fast-forward'
import skullIcon from '@iconify-icons/mdi/skull'
import giftIcon from '@iconify-icons/mdi/gift'
import bugIcon from '@iconify-icons/mdi/bug'
import saveIcon from '@iconify-icons/mdi/content-save'
import fileIcon from '@iconify-icons/mdi/file-document'
import monitorIcon from '@iconify-icons/mdi/monitor-dashboard'
import githubIcon from '@iconify-icons/mdi/github'
import archiveClockIcon from '@iconify-icons/mdi/archive-clock'
import backupRestoreIcon from '@iconify-icons/mdi/backup-restore'

function iconComponent(name: string, icon: IconifyIcon) {
  return defineComponent({
    name,
    props: {
      size: { type: [Number, String] as PropType<number | string>, default: 20 },
    },
    setup(props) {
      return () => h(Icon, { icon, width: props.size, height: props.size, 'aria-hidden': 'true' })
    },
  })
}

export const Minus = iconComponent('MinusIcon', minusIcon)
export const Square = iconComponent('SquareIcon', squareOutlineIcon)
export const X = iconComponent('CloseIcon', closeIcon)
export const RefreshCw = iconComponent('RefreshIcon', refreshIcon)
export const Gamepad2 = iconComponent('GamepadIcon', gamepadIcon)
export const Bot = iconComponent('RobotIcon', robotIcon)
export const TrainFront = iconComponent('TrainIcon', trainIcon)
export const PackageOpen = iconComponent('PackageIcon', packageIcon)
export const Gem = iconComponent('RelicIcon', diamondIcon)
export const Swords = iconComponent('BattleIcon', swordsIcon)
export const ScanSearch = iconComponent('InspectIcon', scanIcon)
export const BugPlay = iconComponent('SpawnIcon', bugPlayIcon)
export const Activity = iconComponent('ActivityIcon', activityIcon)
export const Settings = iconComponent('SettingsIcon', cogIcon)
export const ChevronLeft = iconComponent('ChevronLeftIcon', chevronLeftIcon)
export const ChevronRight = iconComponent('ChevronRightIcon', chevronRightIcon)
export const ShieldAlert = iconComponent('ShieldAlertIcon', shieldAlertIcon)
export const ImageOff = iconComponent('ImageOffIcon', imageOffIcon)
export const Check = iconComponent('CheckIcon', checkIcon)
export const CheckCircle2 = iconComponent('CheckCircleIcon', checkCircleIcon)
export const Info = iconComponent('InfoIcon', infoIcon)
export const TriangleAlert = iconComponent('AlertIcon', alertIcon)
export const XCircle = iconComponent('CloseCircleIcon', closeCircleIcon)
export const FolderOpen = iconComponent('FolderOpenIcon', folderOpenIcon)
export const ShieldCheck = iconComponent('ShieldCheckIcon', shieldCheckIcon)
export const PlugZap = iconComponent('PluginInstallIcon', plugIcon)
export const Plug = iconComponent('PluginToggleIcon', plugOffIcon)
export const Trash2 = iconComponent('DeleteIcon', deleteIcon)
export const Play = iconComponent('PlayIcon', playIcon)
export const Pause = iconComponent('PauseIcon', pauseIcon)
export const OctagonX = iconComponent('StopIcon', stopIcon)
export const Search = iconComponent('SearchIcon', searchIcon)
export const Plus = iconComponent('AddIcon', plusIcon)
export const MapPin = iconComponent('MapPinIcon', mapPinIcon)
export const Download = iconComponent('DownloadIcon', downloadIcon)
export const Shield = iconComponent('ShieldIcon', shieldIcon)
export const Map = iconComponent('MapIcon', mapIcon)
export const Eye = iconComponent('EyeIcon', eyeIcon)
export const Sparkles = iconComponent('EffectsIcon', sparkleIcon)
export const FastForward = iconComponent('FastForwardIcon', fastForwardIcon)
export const Skull = iconComponent('SkullIcon', skullIcon)
export const Gift = iconComponent('RewardIcon', giftIcon)
export const Bug = iconComponent('BugIcon', bugIcon)
export const Save = iconComponent('SaveIcon', saveIcon)
export const Crosshair = iconComponent('CrosshairIcon', scanIcon)
export const FileText = iconComponent('LogIcon', fileIcon)
export const MonitorCog = iconComponent('DisplayIcon', monitorIcon)
export const Github = iconComponent('GitHubIcon', githubIcon)
export const ArchiveClock = iconComponent('ArchiveClockIcon', archiveClockIcon)
export const BackupRestore = iconComponent('BackupRestoreIcon', backupRestoreIcon)
