# ☕ Café Mini PDV

> Ponto de Venda (PDV) leve, reativo e otimizado para dispositivos móveis, construído com **ASP.NET Core Minimal APIs**, **HTMX** e persistência em nuvem com **TiDB Serverless (MySQL)**.

---

## 📌 Sobre o Projeto

O **Café Mini PDV** é uma solução de frente de caixa desenvolvida para agilizar o atendimento presencial e simplificar a gestão de vendas. O sistema combina renderização dinâmica no servidor com HTMX (evitando a complexidade de SPAs pesadas), geração instantânea de cobranças Pix via padrão EMVCo e um painel de controle financeiro detalhado com filtros por período e meio de pagamento.

---

## 🚀 Demonstração em Produção

* **URL:** [https://cafeminipdv.onrender.com](https://cafeminipdv.onrender.com)

> 🔒 **Nota de Acesso:** O sistema encontra-se em ambiente de produção real com restrição de operadoras. O acesso exige autenticação via **Google OAuth 2.0**. Contas não cadastradas na lista de permissões receberão a mensagem de *Acesso Negado* por motivos de segurança e integridade de dados.

---

## ✨ Funcionalidades Principais

* **Frente de Caixa Rápida:**
  * Lançamento de itens com controle de quantidade;
  * Suporte a múltiplos métodos de pagamento (Pix, Dinheiro e Cartão);
  * Aplicação de descontos nominais em reais com recálculo automático;
  * Geração instantânea de QR Code Pix estático (Payload EMVCo com valor e ID de transação).

* **Painel Administrativo e Relatórios:**
  * Listagem e paginação de pedidos realizados;
  * Filtros por data (Hoje, Mês Atual, Período Customizado) e por método de pagamento;
  * Métricas em tempo real: faturamento total e contagem de pedidos concluídos por período;
  * Cancelamento e estorno de vendas com atualização de métricas.

* **Segurança e Infraestrutura:**
  * Autenticação via **Google OAuth 2.0** com cookies de sessão seguros;
  * Autorização estrita baseada em *Allowlist* (lista de e-mails permitidos);
  * Tráfego seguro via HTTPS com suporte a *Forwarded Headers* atrás do proxy reverso;
  * Instalação como **PWA (Progressive Web App)** em smartphones e tablets.

---

## 🛠️ Tecnologias Utilizadas

| Camada | Tecnologia |
| :--- | :--- |
| **Backend** | .NET (C#) / ASP.NET Core Minimal APIs |
| **Frontend** | HTMX, HTML5, CSS3, JavaScript Vanilla |
| **Banco de Dados** | TiDB Cloud Serverless (compatível com MySQL) / Pomelo EF Core |
| **Autenticação** | Google OAuth 2.0 + ASP.NET Cookie Authentication |
| **Containerização** | Docker |
| **Hospedagem** | Render (Web Service) |

---

## 📸 Telas do Sistema

| 🔐 Login & Autenticação | ☕ Balcão & Cobrança Pix | 📊 Dashboard & Relatórios |
| :---: | :---: | :---: |
| <img width="280" alt="Login" src="https://github.com/user-attachments/assets/316197f3-cde4-4732-8e2f-8ce34678ff8c" /> | <img width="280" alt="Cobrança Pix" src="https://github.com/user-attachments/assets/43b97fb3-7e67-4cf0-a106-2c626193c288" /> | <img width="280" alt="Dashboard" src="https://github.com/user-attachments/assets/dc8c278a-e6c6-49df-9588-1fa13de4475b" /> |

---

## ⚙️ Executando Localmente

### Pré-requisitos
* [.NET SDK](https://dotnet.microsoft.com/download)
* Instância MySQL ou TiDB

### Passo a Passo

1. **Clone o repositório:**
   ```bash
   git clone https://github.com/Erickfabio366/CafeMiniPdv.git
   cd CafeMiniPdv
