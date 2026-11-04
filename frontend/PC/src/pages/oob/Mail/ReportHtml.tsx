import React from 'react';

/** 即使后端内容异常，沙箱与独立CSP仍禁止脚本、外链资源、表单和页面跳转。 */
export default function ReportHtml({
  html,
  title,
}: {
  html: string;
  title: string;
}) {
  const policy =
    "default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'";
  return (
    <iframe
      title={title}
      sandbox=""
      referrerPolicy="no-referrer"
      srcDoc={`<meta http-equiv="Content-Security-Policy" content="${policy}">${html}`}
      style={{
        width: '100%',
        height: 560,
        border: '1px solid #d9d9d9',
        background: '#fff',
      }}
    />
  );
}
