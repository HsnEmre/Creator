const STEP_LABELS = {
  content: "Content",
  cast: "Cast",
  storyboard: "Storyboard",
  edit: "Edit"
};

export default function CreatorStepper({ steps, activeStep, onStepChange }) {
  return (
    <nav className="creator-stepper" aria-label="Creator workflow">
      {steps.map((step, index) => {
        const isActive = step.id === activeStep;
        return (
          <button
            type="button"
            className={`creator-step ${isActive ? "active" : ""} ${step.status ? `creator-step-${step.status}` : ""}`}
            key={step.id}
            onClick={() => onStepChange(step.id)}
            aria-current={isActive ? "step" : undefined}
          >
            <span className="creator-step-index">{index + 1}</span>
            <span className="creator-step-copy">
              <b>{step.label || STEP_LABELS[step.id] || step.id}</b>
              <small>{step.summary || step.status || "Ready"}</small>
            </span>
          </button>
        );
      })}
    </nav>
  );
}
