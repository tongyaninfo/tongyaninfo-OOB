ARG PC_BASE=docker.io/library/nginx:stable-alpine
FROM ${PC_BASE}
COPY pc/ /usr/share/nginx/html/
COPY nginx.conf /etc/nginx/conf.d/default.conf
LABEL cn.tongyaninfo.project="oob-management"
EXPOSE 8080
