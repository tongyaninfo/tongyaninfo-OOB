/**
 * 登录接口复用的类型声明。
 *
 * 来源：Ant Design Pro 模板（MIT License）。模板的 services/ant-design-pro
 * 服务层与其余演示类型已从本仓库移除，仅保留业务登录仍使用的两个类型。
 */
declare namespace API {
  type LoginParams = {
    username?: string;
    password?: string;
    autoLogin?: boolean;
    type?: string;
  };

  type LoginResult = {
    status?: string;
    type?: string;
    currentAuthority?: string;
  };
}
