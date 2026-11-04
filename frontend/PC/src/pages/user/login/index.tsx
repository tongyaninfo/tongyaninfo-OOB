import {
  CloudServerOutlined,
  DashboardOutlined,
  LockOutlined,
  SafetyCertificateOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { LoginForm, ProFormText } from '@ant-design/pro-components';
import { Helmet, SelectLang, useModel } from '@umijs/max';
import { Alert, App } from 'antd';
import React, { startTransition, useState } from 'react';
import { Footer } from '@/components';
import { filterSupportedLocales } from '@/locales/supported';
import { useOobIntl } from '@/pages/oob/intl';
import { login } from '@/services/oob/auth';
import Settings from '../../../../config/defaultSettings';
import useStyles from './Login.style';

/** 只接受同源站内地址，登录后的回跳不能将身份流程导向外部站点。 */
const getSafeRedirectUrl = (redirect: string | null): string => {
  if (!redirect?.startsWith('/') || redirect.startsWith('//')) return '/';
  try {
    const parsed = new URL(redirect, window.location.origin);
    return parsed.origin === window.location.origin
      ? `${parsed.pathname}${parsed.search}${parsed.hash}`
      : '/';
  } catch {
    return '/';
  }
};

const Login: React.FC = () => {
  const [userLoginState, setUserLoginState] = useState<API.LoginResult>({});
  const { initialState, setInitialState } = useModel('@@initialState');
  const { styles } = useStyles();
  const { message } = App.useApp();
  const t = useOobIntl();

  const handleSubmit = async (values: API.LoginParams) => {
    try {
      // 身份仍由原后端 Cookie 建立；页面不存储密码，也不替换既有认证合同。
      const result = await login({ ...values, type: 'account' });
      if (result.status !== 'ok') {
        setUserLoginState(result);
        return;
      }
      message.success(t('oob.brand.login.success'));
      const userInfo = await initialState?.fetchUserInfo?.();
      if (userInfo) {
        startTransition(() => {
          setInitialState((state) => ({ ...state, currentUser: userInfo }));
        });
      }
      const params = new URL(window.location.href).searchParams;
      window.location.href = getSafeRedirectUrl(params.get('redirect'));
    } catch {
      message.error(t('oob.brand.login.failure'));
    }
  };

  return (
    <div className={styles.page}>
      <Helmet>
        <title>
          {t('oob.brand.login.title')} - {Settings.title}
        </title>
      </Helmet>
      <header className={styles.header}>
        <img
          className={styles.logo}
          src="/assets/brand/tyinfo-logo-horizontal.png"
          alt="通研技术"
        />
        <div className={styles.lang} data-lang>
          <SelectLang postLocalesData={filterSupportedLocales} />
        </div>
      </header>
      <main className={styles.main}>
        <section className={styles.showcase}>
          <div className={styles.eyebrow}>
            <span className={styles.accent} />
            通研带外系统管理
          </div>
          <h1 className={styles.headline}>
            {t('oob.brand.login.headline')}
            <br />
            <span className={styles.headlineAccent}>
              {t('oob.brand.login.headlineAccent')}
            </span>
          </h1>
          <p className={styles.description}>
            {t('oob.brand.login.description')}
          </p>
          <div className={styles.illustration} aria-hidden="true">
            <div className={styles.rack}>
              {[0, 1, 2].map((index) => (
                <div className={styles.server} key={index}>
                  <CloudServerOutlined className={styles.serverIcon} />
                  <div className={styles.serverLines}>
                    <span />
                    <span />
                  </div>
                  <span className={styles.serverDot} />
                </div>
              ))}
            </div>
          </div>
          <div className={styles.features}>
            <span className={styles.feature}>
              <DashboardOutlined />
              {t('oob.brand.login.monitoring')}
            </span>
            <span className={styles.feature}>
              <CloudServerOutlined />
              {t('oob.brand.login.console')}
            </span>
            <span className={styles.feature}>
              <SafetyCertificateOutlined />
              {t('oob.brand.login.permissions')}
            </span>
          </div>
        </section>
        <section className={styles.formPanel} aria-labelledby="login-title">
          <h2 id="login-title" className={styles.formTitle}>
            {t('oob.brand.login.title')}
          </h2>
          <p className={styles.formDescription}>
            {t('oob.brand.login.welcome')}
          </p>
          <LoginForm
            contentStyle={{ width: '100%', minWidth: 0 }}
            submitter={{
              searchConfig: { submitText: t('oob.brand.login.submit') },
            }}
            onFinish={async (values) => handleSubmit(values as API.LoginParams)}
          >
            {userLoginState.status === 'error' && (
              <Alert
                style={{ marginBottom: 20 }}
                title={t('oob.brand.login.invalid')}
                type="error"
                showIcon
              />
            )}
            <ProFormText
              name="username"
              label={t('oob.brand.login.account')}
              fieldProps={{
                size: 'large',
                prefix: <UserOutlined />,
                autoComplete: 'username',
              }}
              placeholder={t('oob.brand.login.accountPlaceholder')}
              rules={[
                {
                  required: true,
                  message: t('oob.brand.login.accountRequired'),
                },
              ]}
            />
            <ProFormText.Password
              name="password"
              label={t('oob.brand.login.password')}
              fieldProps={{
                size: 'large',
                prefix: <LockOutlined />,
                autoComplete: 'current-password',
              }}
              placeholder={t('oob.brand.login.passwordPlaceholder')}
              rules={[
                {
                  required: true,
                  message: t('oob.brand.login.passwordRequired'),
                },
              ]}
            />
          </LoginForm>
          <p className={styles.hint}>
            <LockOutlined /> {t('oob.brand.login.hint')}
          </p>
        </section>
      </main>
      <Footer />
    </div>
  );
};

export default Login;
