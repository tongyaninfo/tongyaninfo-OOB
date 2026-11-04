import globalHeader from './en-US/globalHeader';
import hardware from './en-US/hardware';
import menu from './en-US/menu';
import network from './en-US/network';
import oob from './en-US/oob';
import pages from './en-US/pages';
import settingDrawer from './en-US/settingDrawer';
import settings from './en-US/settings';

export default {
  ...hardware,
  ...oob,
  'navBar.lang': 'Languages',
  'layout.user.link.help': 'Help',
  'layout.user.link.privacy': 'Privacy',
  'layout.user.link.terms': 'Terms',
  'app.preview.down.block': 'Download this page to your local project',
  ...globalHeader,
  ...menu,
  ...settingDrawer,
  ...settings,
  ...network,
  ...pages,
};
