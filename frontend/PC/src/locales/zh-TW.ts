import globalHeader from './zh-TW/globalHeader';
import hardware from './zh-TW/hardware';
import menu from './zh-TW/menu';
import network from './zh-TW/network';
import oob from './zh-TW/oob';
import pages from './zh-TW/pages';
import settingDrawer from './zh-TW/settingDrawer';
import settings from './zh-TW/settings';

export default {
  ...hardware,
  ...oob,
  'navBar.lang': '語言',
  'layout.user.link.help': '幫助',
  'layout.user.link.privacy': '隱私',
  'layout.user.link.terms': '條款',
  'app.preview.down.block': '下載此頁面到本地項目',
  ...pages,
  ...globalHeader,
  ...menu,
  ...settingDrawer,
  ...settings,
  ...network,
};
