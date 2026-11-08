//! Prototype d'évaluation (#30) : vue Workers (liste, détail, rattachement par clé de jumelage)
//! en Leptos 0.8 + Thaw, contre le contrat `/api/v1/workers*` du central.
use gloo_net::http::Request;
use leptos::prelude::*;
use serde::{Deserialize, Serialize};
use thaw::*;

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
fn WorkersPage() -> impl IntoView {
    let toaster = ToasterInjection::expect_context();
    let reload = RwSignal::new(0u32);
    let workers = LocalResource::new(move || {
        reload.track();
        get_json::<Vec<Worker>>("/api/v1/workers")
    });
    let pending = LocalResource::new(move || {
        reload.track();
        get_json::<Vec<PendingEnrollment>>("/api/v1/workers/enrollment-requests")
    });
    let selected = RwSignal::new(None::<Worker>);
    let dialog_open = RwSignal::new(false);
    let pairing_key = RwSignal::new(String::new());
    let busy = RwSignal::new(false);

    let notify = move |text: String| {
        toaster.dispatch_toast(
            move || view! { <Toast><ToastBody>{text}</ToastBody></Toast> },
            Default::default(),
        )
    };

    let submit = move |_| {
        let key = pairing_key.get_untracked().trim().to_uppercase();
        if key.is_empty() || busy.get_untracked() {
            return;
        }
        busy.set(true);
        leptos::task::spawn_local(async move {
            match approve(key).await {
                Ok(worker) => {
                    notify(format!("Le worker « {} » a rejoint le central.", worker.label()));
                    dialog_open.set(false);
                    pairing_key.set(String::new());
                    reload.update(|n| *n += 1);
                }
                Err(404) => notify(
                    "Cette clé est invalide, expirée ou déjà utilisée. Générez-en une nouvelle depuis le worker."
                        .into(),
                ),
                Err(_) => notify(
                    "Le worker n’a pas pu être accepté. Vérifiez que le serveur est disponible.".into(),
                ),
            }
            busy.set(false);
        });
    };

    view! {
        <main class="page">
            <header class="page-header">
                <h1>"Workers"</h1>
                <Button appearance=ButtonAppearance::Primary on_click=move |_| dialog_open.set(true)>
                    "Rattacher un worker"
                </Button>
            </header>
            <section aria-labelledby="workers-title">
                <h2 id="workers-title">"Membres du central"</h2>
                <Suspense fallback=|| view! { <Spinner label="Chargement des workers" /> }>
                    {move || workers.get().map(|result| match result {
                        Err(error) => view! {
                            <MessageBar intent=MessageBarIntent::Error>
                                <MessageBarBody>{format!("Liste indisponible ({error}).")}</MessageBarBody>
                            </MessageBar>
                        }.into_any(),
                        Ok(list) => view! {
                            <Table attr:aria-label="Workers du central">
                                <TableHeader>
                                    <TableRow>
                                        <TableHeaderCell>"Nom"</TableHeaderCell>
                                        <TableHeaderCell>"État"</TableHeaderCell>
                                        <TableHeaderCell>"Système"</TableHeaderCell>
                                        <TableHeaderCell>"Tâches max"</TableHeaderCell>
                                        <TableHeaderCell>"Action"</TableHeaderCell>
                                    </TableRow>
                                </TableHeader>
                                <TableBody>
                                    {list.into_iter().map(|worker| {
                                        let row = worker.clone();
                                        let label = worker.label();
                                        let state = worker.state();
                                        let aria = format!("Voir le détail de {label}");
                                        let system = worker.inventory.as_ref()
                                            .map(|i| format!("{} {}", i.operating_system, i.architecture))
                                            .unwrap_or_else(|| "—".into());
                                        view! {
                                            <TableRow>
                                                <TableCell>{label}</TableCell>
                                                <TableCell>
                                                    <Badge appearance=BadgeAppearance::Tint>{state}</Badge>
                                                </TableCell>
                                                <TableCell>{system}</TableCell>
                                                <TableCell>{worker.max_concurrent_tasks}</TableCell>
                                                <TableCell>
                                                    <Button
                                                        appearance=ButtonAppearance::Subtle
                                                        attr:aria-label=aria
                                                        on_click=move |_| selected.set(Some(row.clone()))
                                                    >"Détail"</Button>
                                                </TableCell>
                                            </TableRow>
                                        }
                                    }).collect_view()}
                                </TableBody>
                            </Table>
                        }.into_any(),
                    })}
                </Suspense>
            </section>
            <section aria-labelledby="detail-title" class="detail">
                <h2 id="detail-title">"Détail"</h2>
                {move || match selected.get() {
                    None => view! { <p>"Sélectionnez un worker."</p> }.into_any(),
                    Some(worker) => {
                        let inv = worker.inventory.clone();
                        let title = worker.label();
                        view! {
                            <Card>
                                <CardHeader><Body1><b>{title}</b></Body1></CardHeader>
                                <dl>
                                    <dt>"Identifiant"</dt><dd>{worker.id.clone()}</dd>
                                    <dt>"État"</dt><dd>{worker.state()}</dd>
                                    <dt>"Vu pour la dernière fois"</dt><dd>{worker.last_seen_at.clone()}</dd>
                                    <dt>"Processeur"</dt>
                                    <dd>{inv.as_ref().map(|i| format!("{} × {}", i.processor_count,
                                        i.processor_model.clone().unwrap_or_else(|| "inconnu".into())))}</dd>
                                    <dt>"Mémoire"</dt>
                                    <dd>{inv.as_ref().map(|i| format!("{} Mo", i.memory_mb))}</dd>
                                </dl>
                            </Card>
                        }.into_any()
                    }
                }}
            </section>
            <section aria-labelledby="pending-title">
                <h2 id="pending-title">"Demandes de rattachement"</h2>
                <Suspense fallback=|| ()>
                    {move || pending.get().map(|result| match result {
                        Err(_) => view! { <p>"Demandes indisponibles."</p> }.into_any(),
                        Ok(list) if list.is_empty() => view! { <p>"Aucune demande en attente."</p> }.into_any(),
                        Ok(list) => view! {
                            <ul>
                                {list.into_iter().map(|p| view! {
                                    <li>{format!("{} — code {} — expire {}", p.name,
                                        p.verification_code.unwrap_or_else(|| "—".into()), p.expires_at)}
                                        <span hidden>{p.id}</span></li>
                                }).collect_view()}
                            </ul>
                        }.into_any(),
                    })}
                </Suspense>
            </section>
            <Dialog open=dialog_open>
                <DialogSurface>
                    <DialogBody>
                        <DialogTitle>"Rattacher un worker"</DialogTitle>
                        <DialogContent>
                            <Field label="Clé de jumelage" required=true>
                                <Input value=pairing_key placeholder="ABCD-EFGH" attr:autocomplete="off" />
                            </Field>
                        </DialogContent>
                        <DialogActions>
                            <Button on_click=move |_| dialog_open.set(false)>"Annuler"</Button>
                            <Button appearance=ButtonAppearance::Primary disabled=busy on_click=submit>
                                "Approuver"
                            </Button>
                        </DialogActions>
                    </DialogBody>
                </DialogSurface>
            </Dialog>
        </main>
    }
}

fn main() {
    console_error_panic_hook::set_once();
    mount_to_body(|| {
        view! {
            <ConfigProvider>
                <ToasterProvider>
                    <WorkersPage />
                </ToasterProvider>
            </ConfigProvider>
        }
    });
}
