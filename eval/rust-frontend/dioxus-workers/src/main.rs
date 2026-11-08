//! Prototype d'évaluation (#30) : vue Workers (liste, détail, rattachement par clé de jumelage)
//! en Dioxus 0.7 + DioxusLabs/components, contre le contrat `/api/v1/workers*` du central.
use dioxus::prelude::*;
use dioxus_primitives::toast::ToastOptions;
use gloo_net::http::Request;
use serde::{Deserialize, Serialize};

mod components;
use components::badge::{Badge, BadgeVariant};
use components::button::{Button, ButtonVariant};
use components::card::{Card, CardContent, CardHeader, CardTitle};
use components::dialog::{Dialog, DialogDescription, DialogTitle};
use components::input::Input;
use components::label::Label;
use components::toast::ToastProvider;

const THEME: Asset = asset!("/assets/dx-components-theme.css");
const APP_CSS: Asset = asset!("/assets/app.css");

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
struct Inventory {
    operating_system: String,
    architecture: String,
    processor_count: u32,
    memory_mb: u64,
    processor_model: Option<String>,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
struct Worker {
    id: String,
    name: String,
    display_name: Option<String>,
    status: String,
    lifecycle: Option<String>,
    max_concurrent_tasks: u32,
    last_seen_at: String,
    inventory: Option<Inventory>,
}

impl Worker {
    fn label(&self) -> String {
        self.display_name
            .as_deref()
            .map(str::trim)
            .filter(|n| !n.is_empty())
            .unwrap_or(&self.name)
            .to_owned()
    }
    fn state(&self) -> String {
        self.lifecycle.clone().unwrap_or_else(|| self.status.clone())
    }
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
struct PendingEnrollment {
    id: String,
    name: String,
    expires_at: String,
    verification_code: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ApproveByKey {
    pairing_key: String,
}

async fn get_json<T: for<'de> Deserialize<'de>>(url: &str) -> Result<T, String> {
    let response = Request::get(url).send().await.map_err(|e| e.to_string())?;
    if !response.ok() {
        return Err(format!("HTTP {}", response.status()));
    }
    response.json().await.map_err(|e| e.to_string())
}

async fn approve(pairing_key: String) -> Result<Worker, u16> {
    let response = Request::post("/api/v1/workers/enrollment-requests/approve")
        .json(&ApproveByKey { pairing_key })
        .map_err(|_| 0u16)?
        .send()
        .await
        .map_err(|_| 0u16)?;
    if !response.ok() {
        return Err(response.status());
    }
    response.json().await.map_err(|_| 0u16)
}

#[component]
fn WorkersPage() -> Element {
    let toasts = dioxus_primitives::toast::use_toast();
    let mut reload = use_signal(|| 0u32);
    let workers = use_resource(move || async move {
        reload();
        get_json::<Vec<Worker>>("/api/v1/workers").await
    });
    let pending = use_resource(move || async move {
        reload();
        get_json::<Vec<PendingEnrollment>>("/api/v1/workers/enrollment-requests").await
    });
    let mut selected = use_signal(|| None::<Worker>);
    let mut dialog_open = use_signal(|| false);
    let mut pairing_key = use_signal(String::new);
    let mut busy = use_signal(|| false);

    let submit = move |_| {
        let key = pairing_key.peek().trim().to_uppercase();
        if key.is_empty() || *busy.peek() {
            return;
        }
        busy.set(true);
        spawn(async move {
            match approve(key).await {
                Ok(worker) => {
                    toasts.success(
                        format!("Le worker « {} » a rejoint le central.", worker.label()),
                        ToastOptions::new(),
                    );
                    dialog_open.set(false);
                    pairing_key.set(String::new());
                    reload += 1;
                }
                Err(404) => toasts.error(
                    "Cette clé est invalide, expirée ou déjà utilisée. Générez-en une nouvelle depuis le worker."
                        .into(),
                    ToastOptions::new(),
                ),
                Err(_) => toasts.error(
                    "Le worker n’a pas pu être accepté. Vérifiez que le serveur est disponible.".into(),
                    ToastOptions::new(),
                ),
            }
            busy.set(false);
        });
    };

    rsx! {
        main { class: "page",
            header { class: "page-header",
                h1 { "Workers" }
                Button { variant: ButtonVariant::Primary, onclick: move |_| dialog_open.set(true),
                    "Rattacher un worker"
                }
            }
            section { "aria-labelledby": "workers-title",
                h2 { id: "workers-title", "Membres du central" }
                match &*workers.read() {
                    None => rsx! { p { role: "status", "Chargement des workers…" } },
                    Some(Err(error)) => rsx! { p { role: "alert", "Liste indisponible ({error})." } },
                    Some(Ok(list)) => rsx! {
                        // Le kit n'a pas de composant tableau : table HTML sémantique stylée à la main.
                        table { class: "data-table", "aria-label": "Workers du central",
                            thead {
                                tr {
                                    th { scope: "col", "Nom" }
                                    th { scope: "col", "État" }
                                    th { scope: "col", "Système" }
                                    th { scope: "col", "Tâches max" }
                                    th { scope: "col", "Action" }
                                }
                            }
                            tbody {
                                for worker in list.iter().cloned() {
                                    tr { key: "{worker.id}",
                                        td { "{worker.label()}" }
                                        td { Badge { variant: BadgeVariant::Secondary, "{worker.state()}" } }
                                        td {
                                            {worker.inventory.as_ref()
                                                .map(|i| format!("{} {}", i.operating_system, i.architecture))
                                                .unwrap_or_else(|| "—".into())}
                                        }
                                        td { "{worker.max_concurrent_tasks}" }
                                        td {
                                            Button {
                                                variant: ButtonVariant::Ghost,
                                                "aria-label": "Voir le détail de {worker.label()}",
                                                onclick: {
                                                    let worker = worker.clone();
                                                    move |_| selected.set(Some(worker.clone()))
                                                },
                                                "Détail"
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    },
                }
            }
            section { class: "detail", "aria-labelledby": "detail-title",
                h2 { id: "detail-title", "Détail" }
                match selected() {
                    None => rsx! { p { "Sélectionnez un worker." } },
                    Some(worker) => {
                        let inv = worker.inventory.clone();
                        rsx! {
                            Card {
                                CardHeader { CardTitle { "{worker.label()}" } }
                                CardContent {
                                    dl {
                                        dt { "Identifiant" } dd { "{worker.id}" }
                                        dt { "État" } dd { "{worker.state()}" }
                                        dt { "Vu pour la dernière fois" } dd { "{worker.last_seen_at}" }
                                        dt { "Processeur" }
                                        dd {
                                            {inv.as_ref().map(|i| format!("{} × {}", i.processor_count,
                                                i.processor_model.clone().unwrap_or_else(|| "inconnu".into())))}
                                        }
                                        dt { "Mémoire" }
                                        dd { {inv.as_ref().map(|i| format!("{} Mo", i.memory_mb))} }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            section { "aria-labelledby": "pending-title",
                h2 { id: "pending-title", "Demandes de rattachement" }
                match &*pending.read() {
                    None => rsx! {},
                    Some(Err(_)) => rsx! { p { "Demandes indisponibles." } },
                    Some(Ok(list)) if list.is_empty() => rsx! { p { "Aucune demande en attente." } },
                    Some(Ok(list)) => rsx! {
                        ul {
                            for p in list.iter() {
                                li { key: "{p.id}",
                                    "{p.name} — code {p.verification_code.clone().unwrap_or_else(|| \"—\".into())} — expire {p.expires_at}"
                                }
                            }
                        }
                    },
                }
            }
            Dialog {
                open: dialog_open(),
                on_open_change: move |open| dialog_open.set(open),
                DialogTitle { "Rattacher un worker" }
                DialogDescription { "Saisissez la clé de jumelage affichée par le worker." }
                div { class: "field",
                    Label { html_for: "pairing-key", "Clé de jumelage" }
                    Input {
                        id: "pairing-key",
                        required: true,
                        autocomplete: "off",
                        placeholder: "ABCD-EFGH",
                        value: pairing_key(),
                        oninput: move |e: FormEvent| pairing_key.set(e.value()),
                    }
                }
                div { class: "dialog-actions",
                    Button { variant: ButtonVariant::Outline, onclick: move |_| dialog_open.set(false), "Annuler" }
                    Button { variant: ButtonVariant::Primary, disabled: busy(), onclick: submit, "Approuver" }
                }
            }
        }
    }
}

#[component]
fn App() -> Element {
    rsx! {
        document::Stylesheet { href: THEME }
        document::Stylesheet { href: APP_CSS }
        ToastProvider { WorkersPage {} }
    }
}

fn main() {
    dioxus::launch(App);
}
